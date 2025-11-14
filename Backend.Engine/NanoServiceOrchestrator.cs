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

    private List<(FileInfo fileInfo, WorkflowTaskInfo taskInfo)> _plugins = new();
    private Dictionary<string, NanoServiceAgent> _agents = new();

    public NanoServiceOrchestrator(
        BlobContainerClient blobContainerClient,
        IPluginArchiveExtractionUtility archiveExtractor,
        IPluginWorkspaceFactoryUtility workspaceFactory,
        IServiceProvider serviceProvider,
        IWorkflowTaskRepository workflowTaskRepository)
    {
        _blobContainerClient = blobContainerClient;
        _archiveExtractor = archiveExtractor;
        _serviceProvider = serviceProvider;
        _workspaceFactory = workspaceFactory;
        _workflowTaskRepository = workflowTaskRepository;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var tasks = await _workflowTaskRepository.GetAll(cancellationToken);

        foreach (var task in tasks)
        {
            _plugins.Add((await DownloadPlugin(
                task.Name,
                task.Version,
                "TestPlugin",
                cancellationToken), task));
        }

        await base.StartAsync(cancellationToken);
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var (fileInfo, taskInfo) in _plugins)
        {
            var newAgent = _serviceProvider.GetRequiredService<NanoServiceAgent>();
            await newAgent.InitialiseAsync(
                fileInfo,
                Array.Empty<NanoServiceInputChannelModel>(),
                globalState: null,
                stoppingToken);
            _agents.Add(fileInfo.Name, newAgent);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task<FileInfo> DownloadPlugin(
        string pluginName,
        Version pluginVersion,
        string executableName,
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

        return new FileInfo(Path.Combine(workspaceDirectory, executableName));
    }
}
