using System.Collections.Generic;
using Azure.Storage.Blobs;
using Backend.DTOs;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;

namespace Backend.Engine;

public class NanoServiceOrchestrator : BackgroundService
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly IPluginArchiveExtractionUtility _archiveExtractor;
    private readonly IPluginWorkspaceFactoryUtility _workspaceFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkflowTaskRepository _workflowTaskRepository;
    private readonly IAgentManager _agentManager;

    private readonly List<(FileInfo fileInfo, WorkflowTaskInfo taskInfo)> _plugins = new();
    private readonly Dictionary<int, NanoServiceAgent> _agents = new();

    public NanoServiceOrchestrator(
        BlobContainerClient blobContainerClient,
        IPluginArchiveExtractionUtility archiveExtractor,
        IPluginWorkspaceFactoryUtility workspaceFactory,
        IServiceProvider serviceProvider,
        IWorkflowTaskRepository workflowTaskRepository,
        IAgentManager agentManager)
    {
        _blobContainerClient = blobContainerClient;
        _archiveExtractor = archiveExtractor;
        _serviceProvider = serviceProvider;
        _workspaceFactory = workspaceFactory;
        _workflowTaskRepository = workflowTaskRepository;
        _agentManager = agentManager;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var tasks = await _workflowTaskRepository.GetAll(cancellationToken);

        foreach (var task in tasks)
        {
            _plugins.Add((await DownloadPlugin(
                task.Name,
                task.Version,
                task.Path,
                cancellationToken), task));
        }

        await base.StartAsync(cancellationToken);
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialisationTasks = new List<Task>();

        foreach (var (fileInfo, taskInfo) in _plugins)
        {
            var newAgent = _serviceProvider.GetRequiredService<NanoServiceAgent>();
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
        CancellationToken cancellationToken)
    {
        var blobKey = $"{pluginName}_{pluginVersion.ToString()}.zip";
        var blobClient = _blobContainerClient.GetBlobClient(blobKey);

        var workspaceDirectory = await _workspaceFactory.CreateWorkspaceAsync(
            pluginName, pluginVersion, cancellationToken);

        await _archiveExtractor.ExtractAsync(
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
