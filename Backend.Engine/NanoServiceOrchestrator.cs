using System.Collections.Generic;
using Azure.Storage.Blobs;
using Backend.DTOs;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Task = System.Threading.Tasks.Task;

namespace Backend.Engine;

public class NanoServiceOrchestrator : BackgroundService
{
    private readonly IAgentManager _agentManager;
    private readonly BlobContainerClient _blobContainerClient;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    private readonly List<(FileInfo fileInfo, WorkflowTaskInfo taskInfo)> _plugins = [];
    private readonly Dictionary<int, NanoServiceAgent> _agents = [];

    public NanoServiceOrchestrator(
        IAgentManager agentManager,
        BlobContainerClient blobContainerClient,
        ILogger<NanoServiceOrchestrator> logger,
        IServiceScopeFactory serviceScopeFactory)
    {
        _agentManager = agentManager;
        _blobContainerClient = blobContainerClient;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();

        _logger.LogInformation($"{nameof(NanoServiceOrchestrator)} started. Retrieving tasks from database.");
        var workflowTaskRepository = scope.ServiceProvider.GetRequiredService<IWorkflowTaskRepository>();
        var tasks = await workflowTaskRepository.GetAll(cancellationToken);
        _logger.LogInformation("{TaskAmount} tasks retrieved.", tasks.Count);

        _logger.LogInformation("Downloading plugins from blob.");
        foreach (var task in tasks)
        {
            _plugins.Add((await DownloadPlugin(
                task.Name,
                task.Version,
                task.Path,
                scope,
                cancellationToken), task));
        }
        _logger.LogInformation("{PluginAmount} plugins downloaded.", _plugins.Count);

        await base.StartAsync(cancellationToken);
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var initialisationTasks = new List<Task>();

        foreach (var (fileInfo, taskInfo) in _plugins)
        {
            var newAgent = scope.ServiceProvider.GetRequiredService<NanoServiceAgent>();
            initialisationTasks.Add(newAgent.InitialiseAsync(
                fileInfo,
                $"{taskInfo.Name} (Version: {taskInfo.Version})",
                stoppingToken));
            _agents.Add(taskInfo.Id, newAgent);
            _agentManager.RegisterAgent(taskInfo.Id, newAgent);
        }

        await Task.WhenAll(initialisationTasks);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the host is shutting down.
        }

        await StopAgentsAsync(stoppingToken);
    }

    private async Task<FileInfo> DownloadPlugin(
        string pluginName,
        Version pluginVersion,
        string entryPoint,
        IServiceScope serviceScope,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Downloading and extracting version {PluginVersion} of plugin {PluginName}",
            pluginVersion, pluginName);

        var blobKey = $"{pluginName}_{pluginVersion.ToString()}.zip";
        var blobClient = _blobContainerClient.GetBlobClient(blobKey);

        var workspaceFactory = serviceScope.ServiceProvider
            .GetRequiredService<IPluginWorkspaceFactoryUtility>();
        var workspaceDirectory = await workspaceFactory.CreateWorkspaceAsync(
            pluginName, pluginVersion, cancellationToken);

        var archiveExtractor = serviceScope.ServiceProvider
            .GetRequiredService<IPluginArchiveExtractionUtility>();

        await archiveExtractor.ExtractAsync(
            blobClient,
            workspaceDirectory,
            cancellationToken);

        return new FileInfo(Path.Combine(workspaceDirectory, entryPoint));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Stopping {nameof(NanoServiceOrchestrator)}.");
        await StopAgentsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task StopAgentsAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents.Values)
        {
            _logger.LogInformation(
                $"Stopping {nameof(NanoServiceAgent)} " + "{AgentName}", agent.Name);
            await agent.StopAsync(cancellationToken);
        }

        _agents.Clear();
    }
}
