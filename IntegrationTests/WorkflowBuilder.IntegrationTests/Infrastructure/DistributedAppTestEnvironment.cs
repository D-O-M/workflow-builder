using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Azure.Storage.Blobs;
using Backend.Database.State.Objects;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class DistributedAppTestEnvironment : IAsyncDisposable
{
    private const string CosmosResourceName = "integration-cosmos";
    private const string CosmosConnectionName = CosmosDatabaseName;
    private const string CosmosDatabaseName = "enginedb";
    private const string ProcessesContainerName = "processes";
    private const string TasksContainerName = "processsteps";
    private const string PluginsBlobContainerName = "pluginblob";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly DistributedApplication _app;
    private readonly CosmosClient _cosmosClient;
    private readonly BlobContainerClient _pluginBlobContainer;
    private readonly ProcessStepRepository _processStepRepository;
    private readonly ProcessInstanceRepository<Dictionary<string, JsonElement>> _processRepository;

    private DistributedAppTestEnvironment(
        DistributedApplication app,
        CosmosClient cosmosClient,
        BlobContainerClient pluginBlobContainer,
        ProcessStepRepository processStepRepository,
        ProcessInstanceRepository<Dictionary<string, JsonElement>> processRepository,
        HttpClient apiClient,
        string signalRBaseUrl,
        IntegrationTestSettings settings,
        PluginBundleCatalog bundleCatalog)
    {
        _app = app;
        _cosmosClient = cosmosClient;
        _pluginBlobContainer = pluginBlobContainer;
        _processStepRepository = processStepRepository;
        _processRepository = processRepository;

        ApiClient = apiClient;
        SignalRBaseUrl = signalRBaseUrl;
        Settings = settings;
        BundleCatalog = bundleCatalog;
    }

    public IntegrationTestSettings Settings { get; }

    public PluginBundleCatalog BundleCatalog { get; }

    public HttpClient ApiClient { get; }

    public string SignalRBaseUrl { get; }

    public static async Task<DistributedAppTestEnvironment> CreateReadyAsync(CancellationToken cancellationToken)
    {
        var settings = IntegrationTestSettings.Create();
        IntegrationProgressLog.Initialize(settings.RunVersion);
        IntegrationProgressLog.Write($"[integration] Starting Aspire integration environment for version {settings.RunVersion}.");

        var repoRoot = ResolveRepositoryRoot();
        IntegrationProgressLog.Write($"[integration] Repository root resolved to {repoRoot}.");

        IntegrationProgressLog.Write("[integration] Building deterministic plugin bundles.");
        var bundles = await new PluginBundleBuilder(repoRoot).BuildAsync(cancellationToken);
        IntegrationProgressLog.Write($"[integration] Built bundles for {string.Join(", ", bundles.PluginNames)}.");

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Backend_AppHost>(
            [
                "Frontend:Enabled=false",
                $"AzureCosmos:ResourceName={CosmosResourceName}",
                $"AzureCosmos:ConnectionName={CosmosConnectionName}",
                $"AzureCosmos:DatabaseName={CosmosDatabaseName}",
                "AzureCosmos:PersistData=false",
                "AzureStorage:ResourceName=integration-storage",
                "AzureStorage:BlobContainers:Plugins:ResourceName=plugins",
                $"AzureStorage:BlobContainers:Plugins:ContainerName={PluginsBlobContainerName}",
                "AzureStorage:PersistData=false",
                "AzureServiceBus:ResourceName=integration-servicebus",
                "AzureServiceBus:Queues:ProcessStepQueue:ResourceName=process-step-queue",
                "AzureServiceBus:Queues:ProcessStepQueue:ConnectionName=process-step-queue",
                "AzureServiceBus:Queues:ProcessStepQueue:Name=processsteps",
                "AzureServiceBus:Queues:PluginControlQueue:ResourceName=plugin-control-queue",
                "AzureServiceBus:Queues:PluginControlQueue:ConnectionName=plugin-control-queue",
                "AzureServiceBus:Queues:PluginControlQueue:Name=pluginreloadqueue",
                "SqlServer:PersistData=false",
            ],
            (options, _) =>
            {
                options.AllowUnsecuredTransport = true;
                options.EnableResourceLogging = true;
            },
            cancellationToken);

        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddFilter("Aspire.Hosting.Dcp", LogLevel.Warning);
            logging.AddFilter("Aspire.Hosting.Backchannel", LogLevel.Critical);
        });

        var app = await builder.BuildAsync(cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);
        IntegrationProgressLog.Write("[integration] Distributed application built successfully.");

        using var startupLogCapture = BeginStartupLogCapture(app);

        try
        {
            IntegrationProgressLog.Write("[integration] Starting distributed application.");
            await app.StartAsync(cancellationToken)
                .WaitAsync(DefaultTimeout, cancellationToken);
            IntegrationProgressLog.Write("[integration] Distributed application start completed.");

            await WaitForHealthyResourceAsync(app, "backend-api", cancellationToken);
            await WaitForHealthyResourceAsync(app, "backend-engine", cancellationToken);
            await WaitForHealthyResourceAsync(app, "backend-signalr", cancellationToken);
            await WaitForHealthyResourceAsync(app, "plugin-detector", cancellationToken);
            await WaitForHealthyResourceAsync(app, "progress-broadcaster", cancellationToken);
        }
        catch (Exception exception)
        {
            IntegrationProgressLog.Write($"[integration] Distributed startup failed: {exception}");
            var diagnostics = await BuildStartupDiagnosticsAsync(
                app,
                startupLogCapture,
                cancellationToken);

            await app.DisposeAsync();
            throw new InvalidOperationException(
                $"Failed to start the Aspire integration environment.{Environment.NewLine}Root exception:{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}{diagnostics}",
                exception);
        }

        var apiClient = app.CreateHttpClient("backend-api", "http");
        apiClient.Timeout = TimeSpan.FromSeconds(100);
        IntegrationProgressLog.Write($"[integration] backend-api base address: {apiClient.BaseAddress}");

        var signalRBaseUrl = app.CreateHttpClient("backend-signalr", "http").BaseAddress?
            .ToString()
            .TrimEnd('/');
        IntegrationProgressLog.Write($"[integration] backend-signalr base address: {signalRBaseUrl}/");
        if (string.IsNullOrWhiteSpace(signalRBaseUrl))
        {
            throw new InvalidOperationException("SignalR base URL was not resolved by the AppHost.");
        }

        var blobConnectionString = await app.GetConnectionStringAsync("BlobConnection")
            ?? throw new InvalidOperationException("BlobConnection connection string was not resolved by the AppHost.");
        var cosmosConnectionString = await app.GetConnectionStringAsync(CosmosConnectionName)
            ?? throw new InvalidOperationException($"{CosmosConnectionName} connection string was not resolved by the AppHost.");

        var blobContainer = new BlobContainerClient(blobConnectionString, PluginsBlobContainerName);
        await blobContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var cosmosClient = new CosmosClient(
            cosmosConnectionString,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
            });

        var processStepRepository = new ProcessStepRepository(cosmosClient.GetContainer(CosmosDatabaseName, TasksContainerName));
        var processRepository = new ProcessInstanceRepository<Dictionary<string, JsonElement>>(
            cosmosClient.GetContainer(CosmosDatabaseName, ProcessesContainerName));

        var environment = new DistributedAppTestEnvironment(
            app,
            cosmosClient,
            blobContainer,
            processStepRepository,
            processRepository,
            apiClient,
            signalRBaseUrl,
            settings,
            bundles);

        try
        {
            IntegrationProgressLog.Write("[integration] Waiting for /Tasks endpoint.");
            await environment.WaitForTasksEndpointAsync(cancellationToken);

            IntegrationProgressLog.Write("[integration] Uploading deterministic plugin bundles.");
            await environment.RegisterAllBundlesAsync(cancellationToken);
            await environment.WaitForPluginRegistrationAsync(cancellationToken);
            IntegrationProgressLog.Write("[integration] Aspire integration environment is ready.");
        }
        catch (Exception exception)
        {
            IntegrationProgressLog.Write($"[integration] Runtime readiness failed: {exception}");
            var diagnostics = await BuildStartupDiagnosticsAsync(
                app,
                startupLogCapture,
                cancellationToken);

            await environment.DisposeAsync();
            throw new InvalidOperationException(
                $"Aspire integration environment reached healthy startup but failed during runtime readiness.{Environment.NewLine}Root exception:{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}{diagnostics}",
                exception);
        }

        return environment;
    }

    private static string ResolveRepositoryRoot()
    {
        foreach (var candidateRoot in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(candidateRoot));

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "BuildScripts", "bundle-plugins.ps1")) &&
                    Directory.Exists(Path.Combine(current.FullName, "Backend.AppHost")) &&
                    File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException(
            $"Could not resolve the repository root from '{Directory.GetCurrentDirectory()}' or '{AppContext.BaseDirectory}'.");
    }

    public async Task<List<TaskDefinitionViewModel>> GetTasksAsync(CancellationToken cancellationToken)
    {
        using var response = await ApiClient.GetAsync("/Tasks", cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GET /Tasks returned {(int)response.StatusCode} ({response.StatusCode}).{Environment.NewLine}{responseBody}");
        }

        return JsonSerializer.Deserialize<List<TaskDefinitionViewModel>>(
                   responseBody,
                   JsonSerializerOptions.Web)
               ?? [];
    }

    public HubConnection CreateSignalRConnection() =>
        new HubConnectionBuilder()
            .WithUrl($"{SignalRBaseUrl}/hubs/processes")
            .WithAutomaticReconnect()
            .Build();

    public async Task UploadBundleAsync(
        string pluginName,
        string version,
        CancellationToken cancellationToken)
    {
        var bundlePath = BundleCatalog[pluginName];
        var blobName = $"{pluginName}_{version}.zip";

        await using var bundleStream = File.OpenRead(bundlePath);
        await _pluginBlobContainer
            .GetBlobClient(blobName)
            .UploadAsync(bundleStream, overwrite: true, cancellationToken);

        IntegrationProgressLog.Write($"[integration] Uploaded plugin bundle {blobName}.");
    }

    public async Task WaitForPluginRegistrationAsync(CancellationToken cancellationToken)
    {
        await WaitForAsync(
            async ct =>
            {
                var tasks = await GetTasksAsync(ct);
                return BundleCatalog.PluginNames.All(pluginName =>
                    tasks.Any(task =>
                        task.Name == pluginName &&
                        task.Versions.Any(version => VersionsMatch(version.Version, Settings.RunVersion))));
            },
            $"plugin registration for version {Settings.RunVersion}",
            cancellationToken);
    }

    public async Task WaitForPluginRegistrationAsync(
        string pluginName,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null)
    {
        await WaitForAsync(
            async ct =>
            {
                var tasks = await GetTasksAsync(ct);
                return tasks.Any(task =>
                    task.Name == pluginName &&
                    task.Versions.Any(version => VersionsMatch(version.Version, Settings.RunVersion)));
            },
            $"plugin registration for {pluginName} {Settings.RunVersion}",
            cancellationToken,
            timeoutOverride);
    }

    public async Task<IReadOnlyList<ProcessStepRecord>> GetProcessStepStateAsync(
        Guid processId,
        CancellationToken cancellationToken) =>
        await _processStepRepository.GetProcessStepsForProcess(processId, cancellationToken);

    public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateAsync(
        Guid processId,
        int blueprintVersionId,
        CancellationToken cancellationToken) =>
        _processRepository.GetProcessStateById(processId, blueprintVersionId, cancellationToken);

    private async Task RegisterAllBundlesAsync(CancellationToken cancellationToken)
    {
        foreach (var pluginName in BundleCatalog.PluginNames)
        {
            await UploadBundleAsync(pluginName, Settings.RunVersion, cancellationToken);
            await WaitForPluginRegistrationAsync(
                pluginName,
                cancellationToken,
                timeoutOverride: TimeSpan.FromSeconds(60));
        }
    }

    private async Task WaitForTasksEndpointAsync(CancellationToken cancellationToken)
    {
        await WaitForAsync(
            async ct =>
            {
                try
                {
                    await GetTasksAsync(ct);
                    return true;
                }
                catch (Exception exception)
                {
                    IntegrationProgressLog.Write($"[integration] /Tasks probe failed: {exception.Message}");
                    return false;
                }
            },
            "the /Tasks endpoint to become available",
            cancellationToken);

        IntegrationProgressLog.Write("[integration] /Tasks endpoint is available.");
    }

    private static async Task WaitForHealthyResourceAsync(
        DistributedApplication app,
        string resourceName,
        CancellationToken cancellationToken)
    {
        IntegrationProgressLog.Write($"[integration] Waiting for resource '{resourceName}' to become healthy.");
        await app.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);
        IntegrationProgressLog.Write($"[integration] Resource '{resourceName}' is healthy.");
    }

    private async Task WaitForAsync(
        Func<CancellationToken, Task<bool>> predicate,
        string description,
        CancellationToken cancellationToken,
        TimeSpan? timeoutOverride = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutOverride ?? Settings.ResourceTimeout);

        while (true)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();

            if (await predicate(timeoutCts.Token))
            {
                return;
            }

            try
            {
                await Task.Delay(Settings.PollInterval, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    public async ValueTask DisposeAsync()
    {
        ApiClient.Dispose();
        _cosmosClient.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static StartupLogCapture BeginStartupLogCapture(DistributedApplication app)
    {
        var loggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        var cancellationSource = new CancellationTokenSource();
        var resourceLogs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var resourceNames = new[]
        {
            "backend-api",
            "backend-database-migrations",
            "backend-engine",
            "backend-signalr",
            "grafana-alloy",
            "loki",
            "prometheus",
            "progress-broadcaster",
            "plugin-detector",
            "sqlserver",
            "integration-cosmos",
            "integration-servicebus",
            "integration-storage"
        };

        var tasks = resourceNames
            .Select(resourceName => Task.Run(
                async () =>
                {
                    var lines = new List<string>();
                    lock (resourceLogs)
                    {
                        resourceLogs[resourceName] = lines;
                    }

                    try
                    {
                        await foreach (var entry in loggerService.WatchAsync(resourceName).WithCancellation(cancellationSource.Token))
                        {
                            var text = entry?.ToString();
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                continue;
                            }

                            lock (lines)
                            {
                                lines.Add(text);
                                if (lines.Count > 80)
                                {
                                    lines.RemoveRange(0, lines.Count - 80);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception watchException)
                    {
                        lock (lines)
                        {
                            lines.Add($"<log capture failed: {watchException.Message}>");
                        }
                    }
                },
                CancellationToken.None))
            .ToArray();

        return new StartupLogCapture(cancellationSource, resourceLogs, tasks);
    }

    private static async Task<string> BuildStartupDiagnosticsAsync(
        DistributedApplication app,
        StartupLogCapture startupLogCapture,
        CancellationToken cancellationToken)
    {
        startupLogCapture.CancellationSource.Cancel();
        try
        {
            await Task.WhenAll(startupLogCapture.Tasks).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch
        {
        }

        var resourceNames = new[]
        {
            "backend-api",
            "backend-database-migrations",
            "backend-engine",
            "backend-signalr",
            "grafana-alloy",
            "loki",
            "prometheus",
            "progress-broadcaster",
            "plugin-detector",
            "sqlserver",
            "integration-cosmos",
            "integration-servicebus",
            "integration-storage"
        };

        var diagnostics = new List<string>
        {
            "Resource state snapshot:"
        };

        ResourceNotificationService? resourceNotifications = null;
        try
        {
            resourceNotifications = app.ResourceNotifications;
        }
        catch (ObjectDisposedException)
        {
            diagnostics.Add("- resource notifications unavailable because the distributed application was already disposed");
        }

        foreach (var resourceName in resourceNames)
        {
            if (resourceNotifications is null ||
                !resourceNotifications.TryGetCurrentState(resourceName, out var resourceEvent))
            {
                diagnostics.Add($"- {resourceName}: no state available");
                continue;
            }

            var snapshot = resourceEvent.Snapshot;
            diagnostics.Add(
                $"- {resourceName}: state={snapshot.State?.Text ?? "<null>"}; health={snapshot.HealthStatus?.ToString() ?? "<null>"}; exitCode={snapshot.ExitCode?.ToString() ?? "<null>"}");

            if (snapshot.Properties.Any())
            {
                foreach (var property in snapshot.Properties.OrderBy(property => property.Name))
                {
                    diagnostics.Add($"  property[{property.Name}]={property.Value}");
                }
            }

            var containerId = snapshot.Properties
                .FirstOrDefault(property => string.Equals(property.Name, "container.id", StringComparison.OrdinalIgnoreCase))
                ?.Value?
                .ToString();

            if (!string.IsNullOrWhiteSpace(containerId))
            {
                var dockerLogs = TryRunCommand("docker", $"logs {containerId}");
                if (!string.IsNullOrWhiteSpace(dockerLogs))
                {
                    diagnostics.Add($"  docker logs for {resourceName}:");
                    diagnostics.AddRange(
                        dockerLogs
                            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .TakeLast(20)
                            .Select(line => $"    {line}"));
                }
            }

            if (!snapshot.HealthReports.IsDefaultOrEmpty)
            {
                foreach (var report in snapshot.HealthReports)
                {
                    diagnostics.Add(
                        $"  health[{report.Name}]: status={report.Status?.ToString() ?? "<null>"}; description={report.Description ?? "<none>"}; exception={report.ExceptionText ?? "<none>"}");
                }
            }

            if (!startupLogCapture.ResourceLogs.TryGetValue(resourceName, out var lines))
            {
                continue;
            }

            List<string> capturedLines;
            lock (lines)
            {
                capturedLines = lines.TakeLast(20).ToList();
            }

            if (capturedLines.Count == 0)
            {
                continue;
            }

            diagnostics.Add($"  recent logs for {resourceName}:");
            diagnostics.AddRange(capturedLines.Select(line => $"    {line}"));
        }

        return string.Join(Environment.NewLine, diagnostics);
    }

    private static bool VersionsMatch(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.Equals(rightVersion);
        }

        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string? TryRunCommand(string fileName, string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            return string.IsNullOrWhiteSpace(output)
                ? error
                : string.IsNullOrWhiteSpace(error)
                    ? output
                    : $"{output}{Environment.NewLine}{error}";
        }
        catch
        {
            return null;
        }
    }

    private sealed record StartupLogCapture(
        CancellationTokenSource CancellationSource,
        Dictionary<string, List<string>> ResourceLogs,
        Task[] Tasks) : IDisposable
    {
        public void Dispose() => CancellationSource.Dispose();
    }
}
