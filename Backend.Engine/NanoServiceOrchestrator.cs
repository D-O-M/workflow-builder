using System.Collections.Generic;
using Azure.Storage.Blobs;
using Backend.DTOs;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;

namespace Backend.Engine;

public class NanoServiceOrchestrator : BackgroundService
{
    private readonly IAgentManager _agentManager;
    private readonly BlobContainerClient _blobContainerClient;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    private readonly List<(FileInfo fileInfo, WorkflowTaskInfo taskInfo)> _plugins = new();
    private readonly Dictionary<int, NanoServiceAgent> _agents = new();

    public NanoServiceOrchestrator(
        IAgentManager agentManager,
        BlobContainerClient blobContainerClient,
        IServiceScopeFactory serviceScopeFactory)
    {
        _agentManager = agentManager;
        _blobContainerClient = blobContainerClient;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var workflowTaskRepository = scope.ServiceProvider.GetRequiredService<IWorkflowTaskRepository>();
        var tasks = await workflowTaskRepository.GetAll(cancellationToken);

        foreach (var task in tasks)
        {
            _plugins.Add((await DownloadPlugin(
                task.Name,
                task.Version,
                task.Path,
                scope,
                cancellationToken), task));
        }

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
        await StopAgentsAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    private async Task StopAgentsAsync(CancellationToken cancellationToken)
    {
        foreach (var agent in _agents.Values)
        {
            await agent.StopAsync(cancellationToken);
        }

        _agents.Clear();
    }
}
