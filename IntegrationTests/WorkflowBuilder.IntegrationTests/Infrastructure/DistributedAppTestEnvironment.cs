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
using Backend.Topology;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class DistributedAppTestEnvironment : IAsyncDisposable
{
    private const string CosmosResourceName = "integration-cosmos";
    private const string CosmosConnectionName = CosmosDatabaseName;
    private const string CosmosDatabaseName = WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;
    private static readonly string ProcessesContainerName = WorkflowBuilderTopology.Cosmos.Containers.Processes.Name;
    private static readonly string TasksContainerName = WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name;
    private static readonly string PluginsBlobContainerName = WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerName;
    private readonly DistributedApplication _app;
    private readonly CosmosClient _cosmosClient;
    private readonly BlobContainerClient _pluginBlobContainer;
    private readonly ProcessStepRepository _processStepRepository;
    private readonly ProcessInstanceRepository<Dictionary<string, JsonElement>> _processRepository;
    private readonly StartupLogCapture _resourceLogCapture;

    private DistributedAppTestEnvironment(
        DistributedApplication app,
        CosmosClient cosmosClient,
        BlobContainerClient pluginBlobContainer,
        ProcessStepRepository processStepRepository,
        ProcessInstanceRepository<Dictionary<string, JsonElement>> processRepository,
        HttpClient apiClient,
        string signalRBaseUrl,
        IntegrationTestSettings settings,
        PluginBundleCatalog bundleCatalog,
        StartupLogCapture resourceLogCapture)
    {
        _app = app;
        _cosmosClient = cosmosClient;
        _pluginBlobContainer = pluginBlobContainer;
        _processStepRepository = processStepRepository;
        _processRepository = processRepository;
        _resourceLogCapture = resourceLogCapture;

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
                $"{WorkflowBuilderTopology.Cosmos.ResourceNameKey}={CosmosResourceName}",
                $"{WorkflowBuilderTopology.Cosmos.ConnectionNameKey}={CosmosConnectionName}",
                $"{WorkflowBuilderTopology.Cosmos.DatabaseNameKey}={CosmosDatabaseName}",
                "AzureCosmos:PersistData=false",
                "IntegrationTests:Enabled=true",
                $"{WorkflowBuilderTopology.Storage.ResourceNameKey}=integration-storage",
                $"{WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ResourceNameKey}={WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ResourceName}",
                $"{WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey}={PluginsBlobContainerName}",
                $"{WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ResourceNameKey}={WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ResourceName}",
                $"{WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameKey}={WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerName}",
                "AzureStorage:PersistData=false",
                $"{WorkflowBuilderTopology.ServiceBus.ResourceNameKey}=integration-servicebus",
                $"{WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ResourceNameKey}={WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ResourceName}",
                $"{WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameKey}={WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionName}",
                $"{WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey}={WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueName}",
                $"{WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ResourceNameKey}={WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ResourceName}",
                $"{WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameKey}={WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionName}",
                $"{WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameKey}={WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueName}",
                "SqlServer:PersistData=false",
                "PluginSeeding:Enabled=false",
                "BlueprintSeeding:Enabled=false",
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
            .WaitAsync(settings.ResourceTimeout, cancellationToken);
        IntegrationProgressLog.Write("[integration] Distributed application built successfully.");

        var resourceLogCapture = BeginStartupLogCapture(app);

        try
        {
            IntegrationProgressLog.Write("[integration] Starting distributed application.");
            await app.StartAsync(cancellationToken)
                .WaitAsync(settings.AppHostStartupTimeout, cancellationToken);
            IntegrationProgressLog.Write("[integration] Distributed application start completed.");

            await WaitForHealthyResourceAsync(app, "backend-api", settings, cancellationToken);
            await WaitForHealthyResourceAsync(app, "backend-engine", settings, cancellationToken);
            await WaitForHealthyResourceAsync(app, "backend-signalr", settings, cancellationToken);
            await WaitForHealthyResourceAsync(app, "plugin-detector", settings, cancellationToken);
            await WaitForHealthyResourceAsync(app, "progress-broadcaster", settings, cancellationToken);
        }
        catch (Exception exception)
        {
            IntegrationProgressLog.Write($"[integration] Distributed startup failed: {exception}");
            var diagnostics = await BuildStartupDiagnosticsWithTimeoutAsync(
                app,
                resourceLogCapture,
                settings);

            IntegrationProgressLog.Write("[integration] Stopping distributed application after startup failure.");
            await StopAndDisposeAppBestEffortAsync(app, settings);
            resourceLogCapture.Dispose();
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

        var blobConnectionString = await app.GetConnectionStringAsync(WorkflowBuilderTopology.Storage.BlobConnectionName)
            ?? throw new InvalidOperationException($"{WorkflowBuilderTopology.Storage.BlobConnectionName} connection string was not resolved by the AppHost.");
        var cosmosConnectionString = await app.GetConnectionStringAsync(CosmosConnectionName)
            ?? throw new InvalidOperationException($"{CosmosConnectionName} connection string was not resolved by the AppHost.");

        var blobContainer = new BlobContainerClient(blobConnectionString, PluginsBlobContainerName);
        await blobContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        var cosmosClient = new CosmosClient(
            cosmosConnectionString,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                RequestTimeout = TimeSpan.FromSeconds(10),
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
            bundles,
            resourceLogCapture);

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
            var diagnostics = await BuildStartupDiagnosticsWithTimeoutAsync(
                app,
                resourceLogCapture,
                settings);

            IntegrationProgressLog.Write("[integration] Stopping distributed application after runtime readiness failure.");
            await environment.DisposeAsync().AsTask().WaitAsync(settings.AppHostShutdownTimeout * 2);
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
            .WithUrl($"{SignalRBaseUrl}{WorkflowBuilderTopology.Realtime.HubRoute}")
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

    public Task<ProcessStepRecord> UpdateProcessStepStateAsync(
        ProcessStepRecord processStep,
        CancellationToken cancellationToken) =>
        _processStepRepository.UpdateProcessStep(processStep, cancellationToken);

    public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateAsync(
        Guid processId,
        int blueprintVersionId,
        CancellationToken cancellationToken) =>
        _processRepository.GetProcessStateById(processId, blueprintVersionId, cancellationToken);

    public void WriteRecentResourceLogs(string reason, params string[] resourceNames)
    {
        IntegrationProgressLog.Write($"[integration] Runtime diagnostics requested: {reason}.");

        var requestedNames = resourceNames.Length == 0
            ? _resourceLogCapture.ResourceLogs.Keys.ToArray()
            : resourceNames;

        foreach (var resourceName in requestedNames)
        {
            if (!_resourceLogCapture.ResourceLogs.TryGetValue(resourceName, out var lines))
            {
                IntegrationProgressLog.Write($"[integration] No resource log capture exists for {resourceName}.");
                continue;
            }

            List<string> capturedLines;
            lock (lines)
            {
                capturedLines = lines.TakeLast(80).ToList();
            }

            IntegrationProgressLog.Write($"[integration] Recent logs for {resourceName}:");
            if (capturedLines.Count == 0)
            {
                IntegrationProgressLog.Write("  <no captured log lines>");
                continue;
            }

            foreach (var line in capturedLines)
            {
                IntegrationProgressLog.Write($"  {line}");
            }
        }
    }

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
        IntegrationTestSettings settings,
        CancellationToken cancellationToken)
    {
        IntegrationProgressLog.Write($"[integration] Waiting for resource '{resourceName}' to become healthy.");
        await app.ResourceNotifications.WaitForResourceHealthyAsync(resourceName, cancellationToken)
            .WaitAsync(settings.ResourceTimeout, cancellationToken);
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
        await StopAndDisposeAppBestEffortAsync(_app, Settings);
        _resourceLogCapture.Dispose();
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
                                if (lines.Count > 400)
                                {
                                    lines.RemoveRange(0, lines.Count - 400);
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

    private static async Task<string> BuildStartupDiagnosticsWithTimeoutAsync(
        DistributedApplication app,
        StartupLogCapture startupLogCapture,
        IntegrationTestSettings settings)
    {
        try
        {
            var diagnostics = await BuildStartupDiagnosticsAsync(
                    app,
                    startupLogCapture,
                    settings)
                .WaitAsync(settings.DiagnosticsTimeout);

            IntegrationProgressLog.Write("[integration] Startup diagnostics captured.");
            return diagnostics;
        }
        catch (Exception exception)
        {
            var message = $"Startup diagnostics could not be captured within {settings.DiagnosticsTimeout}: {exception.Message}";
            IntegrationProgressLog.Write($"[integration] {message}");
            return message;
        }
    }

    private static async Task<string> BuildStartupDiagnosticsAsync(
        DistributedApplication app,
        StartupLogCapture startupLogCapture,
        IntegrationTestSettings settings)
    {
        startupLogCapture.CancellationSource.Cancel();
        using var diagnosticsCts = new CancellationTokenSource(settings.DiagnosticsTimeout);

        try
        {
            await Task.WhenAll(startupLogCapture.Tasks).WaitAsync(TimeSpan.FromSeconds(2), diagnosticsCts.Token);
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

    private static async Task StopAndDisposeAppBestEffortAsync(
        DistributedApplication app,
        IntegrationTestSettings settings)
    {
        try
        {
            await app.StopAsync().WaitAsync(settings.AppHostShutdownTimeout);
        }
        catch (Exception exception)
        {
            IntegrationProgressLog.Write($"[integration] AppHost stop did not complete cleanly: {exception.Message}");
        }

        try
        {
            await app.DisposeAsync().AsTask().WaitAsync(settings.AppHostShutdownTimeout);
        }
        catch (Exception exception)
        {
            IntegrationProgressLog.Write($"[integration] AppHost dispose did not complete cleanly: {exception.Message}");
        }
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
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
            }

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
        public void Dispose()
        {
            CancellationSource.Cancel();
            CancellationSource.Dispose();
        }
    }
}
