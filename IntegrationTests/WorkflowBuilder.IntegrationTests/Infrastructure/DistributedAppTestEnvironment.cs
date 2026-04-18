using System.Net.Http.Json;
using System.Text.Json;
using Backend.Database.Relational;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Models;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Backend.Services;
using Backend.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class DistributedAppTestEnvironment : IAsyncDisposable
{
    private readonly string _databasePath;
    private readonly string _pluginStateRootPath;
    private readonly string _sqliteConnectionString;
    private readonly InMemoryTaskInstanceRepository _taskRepository;
    private readonly InMemoryWorkflowInstanceRepository _workflowRepository;
    private readonly WorkflowApiFactory _factory;

    private DistributedAppTestEnvironment(
        string databasePath,
        string pluginStateRootPath,
        string sqliteConnectionString,
        IntegrationTestSettings settings,
        PluginBundleCatalog bundleCatalog,
        InMemoryTaskInstanceRepository taskRepository,
        InMemoryWorkflowInstanceRepository workflowRepository,
        InMemoryWorkflowTaskQueue queue,
        InMemoryPluginReloadQueue pluginReloadQueue,
        FilePluginBundleSource pluginBundleSource)
    {
        _databasePath = databasePath;
        _pluginStateRootPath = pluginStateRootPath;
        _sqliteConnectionString = sqliteConnectionString;
        _taskRepository = taskRepository;
        _workflowRepository = workflowRepository;

        Settings = settings;
        BundleCatalog = bundleCatalog;
        Queue = queue;
        PluginReloadQueue = pluginReloadQueue;
        PluginBundleSource = pluginBundleSource;
        _factory = new WorkflowApiFactory(
            sqliteConnectionString,
            pluginStateRootPath,
            taskRepository,
            workflowRepository,
            queue,
            pluginReloadQueue,
            pluginBundleSource);
    }

    public IntegrationTestSettings Settings { get; }

    public PluginBundleCatalog BundleCatalog { get; }

    public InMemoryWorkflowTaskQueue Queue { get; }

    public InMemoryPluginReloadQueue PluginReloadQueue { get; }

    public FilePluginBundleSource PluginBundleSource { get; }

    public HttpClient ApiClient { get; private set; } = null!;

    public static async Task<DistributedAppTestEnvironment> CreateReadyAsync(
        CancellationToken cancellationToken)
    {
        var settings = IntegrationTestSettings.Create();
        IntegrationProgressLog.Initialize(settings.RunVersion);
        IntegrationProgressLog.Write($"[integration] Starting hybrid integration environment for version {settings.RunVersion}.");

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        IntegrationProgressLog.Write($"[integration] Repository root resolved to {repoRoot}.");

        IntegrationProgressLog.Write("[integration] Building deterministic plugin bundles.");
        var bundles = await new PluginBundleBuilder(repoRoot).BuildAsync(cancellationToken);
        IntegrationProgressLog.Write($"[integration] Built bundles for {string.Join(", ", bundles.PluginNames)}.");

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "workflow-builder-integration",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        IntegrationProgressLog.Write($"[integration] Using temporary root {tempRoot}.");

        var databasePath = Path.Combine(tempRoot, "workflow-builder.integration.sqlite");
        var pluginStateRootPath = Path.Combine(tempRoot, "plugin-state");
        Directory.CreateDirectory(pluginStateRootPath);

        var sqliteConnectionString = $"Data Source={databasePath}";
        var queue = new InMemoryWorkflowTaskQueue();
        var pluginReloadQueue = new InMemoryPluginReloadQueue();
        var taskRepository = new InMemoryTaskInstanceRepository(pluginStateRootPath);
        var workflowRepository = new InMemoryWorkflowInstanceRepository(pluginStateRootPath);
        var pluginBundleSource = new FilePluginBundleSource(bundles);

        var environment = new DistributedAppTestEnvironment(
            databasePath,
            pluginStateRootPath,
            sqliteConnectionString,
            settings,
            bundles,
            taskRepository,
            workflowRepository,
            queue,
            pluginReloadQueue,
            pluginBundleSource);

        IntegrationProgressLog.Write("[integration] Resetting relational database.");
        await environment.ResetRelationalStateAsync(cancellationToken);
        IntegrationProgressLog.Write("[integration] Registering deterministic plugin bundles.");
        await environment.RegisterAllBundlesAsync(cancellationToken);

        IntegrationProgressLog.Write("[integration] Creating in-process API host.");
        environment.ApiClient = environment._factory.CreateClient();
        IntegrationProgressLog.Write("[integration] Waiting for /Tasks endpoint.");
        await environment.WaitForTasksEndpointAsync(cancellationToken);
        IntegrationProgressLog.Write("[integration] Waiting for engine readiness.");
        await environment.WaitForEngineReadinessAsync(cancellationToken);
        IntegrationProgressLog.Write("[integration] Hybrid integration environment is ready.");

        return environment;
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

    public Task UploadBundleAsync(
        string pluginName,
        string version,
        CancellationToken cancellationToken) =>
        RegisterBundleAsync(pluginName, version, cancellationToken);

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

    public Task<IReadOnlyList<TaskObject>> GetTaskStateAsync(
        Guid workflowInstanceId,
        CancellationToken cancellationToken) =>
        Task.FromResult((IReadOnlyList<TaskObject>)_taskRepository.GetTasksForWorkflow(workflowInstanceId));

    public Task<WorkflowObject<Dictionary<string, JsonElement>>> GetWorkflowStateAsync(
        Guid workflowInstanceId,
        int blueprintVersionId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_workflowRepository.GetWorkflow(workflowInstanceId, blueprintVersionId));

    private async Task RegisterAllBundlesAsync(CancellationToken cancellationToken)
    {
        foreach (var pluginName in BundleCatalog.PluginNames)
        {
            IntegrationProgressLog.Write($"[integration] Registering plugin {pluginName} {Settings.RunVersion}.");
            await RegisterBundleAsync(pluginName, Settings.RunVersion, cancellationToken);
        }
    }

    private async Task RegisterBundleAsync(
        string pluginName,
        string version,
        CancellationToken cancellationToken)
    {
        await using var provider = CreatePluginRegistrationProvider();
        using var scope = provider.CreateScope();
        var bundleProcessor = scope.ServiceProvider.GetRequiredService<IPluginBundleProcessingService>();

        await bundleProcessor.ProcessAsync(
            new PluginBlobInfo(
                $"{pluginName}_{version}.zip",
                pluginName,
                Version.Parse(version)),
            cancellationToken);

        IntegrationProgressLog.Write($"[integration] Registered plugin {pluginName} {version}.");
    }

    private ServiceProvider CreatePluginRegistrationProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextPool<WorkflowDbContext>(options =>
            options.UseSqlite(_sqliteConnectionString));
        services.AddRelationalDataRepositories();
        services.AddSingleton<IPluginBundleSource>(PluginBundleSource);
        services.AddSingleton<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();
        services.AddSingleton<IPluginManifestParsingUtility, PluginManifestParsingUtility>();
        services.AddSingleton<IPluginSchemaAggregationUtility, PluginSchemaAggregationUtility>();
        services.AddSingleton<IPluginReloadQueueWriter>(PluginReloadQueue);
        services.AddSingleton<IPluginBundleProcessingService, PluginBundleProcessingService>();
        return services.BuildServiceProvider();
    }

    private async Task ResetRelationalStateAsync(CancellationToken cancellationToken)
    {
        await using var provider = CreatePluginRegistrationProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        await dbContext.Database.EnsureDeletedAsync(cancellationToken);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        IntegrationProgressLog.Write("[integration] Relational schema recreated.");
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
                catch
                {
                    return false;
                }
            },
            "the /Tasks endpoint to become available",
            cancellationToken);

        IntegrationProgressLog.Write("[integration] /Tasks endpoint is available.");
    }

    private async Task WaitForEngineReadinessAsync(CancellationToken cancellationToken)
    {
        var tasks = await GetTasksAsync(cancellationToken);
        IntegrationProgressLog.Write($"[integration] Retrieved {tasks.Count} task definitions while waiting for engine readiness.");
        var expectedAgentIds = BundleCatalog.PluginNames
            .Select(pluginName => tasks
                .Single(task => task.Name == pluginName)
                .Versions
                .Single(taskVersion => VersionsMatch(taskVersion.Version, Settings.RunVersion))
                .Id)
            .ToList();

        IntegrationProgressLog.Write($"[integration] Waiting for engine agents {string.Join(", ", expectedAgentIds)}.");

        await WaitForAsync(
            ct =>
            {
                var agentManager = _factory.Services.GetRequiredService<IAgentManager>();
                return Task.FromResult(expectedAgentIds.All(agentManager.AgentExists));
            },
            "the engine to load all registered plugin agents",
            cancellationToken);

        IntegrationProgressLog.Write("[integration] Engine agents are ready.");
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
        ApiClient?.Dispose();
        await _factory.DisposeAsync();

        TryDeletePath(_databasePath);
        TryDeletePath(_pluginStateRootPath);
    }

    private static bool VersionsMatch(string left, string right)
    {
        if (Version.TryParse(left, out var leftVersion) && Version.TryParse(right, out var rightVersion))
        {
            return leftVersion.Equals(rightVersion);
        }

        return string.Equals(left, right, StringComparison.Ordinal);
    }

    private static void TryDeletePath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for test artifacts.
        }
    }
}
