using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Backend.Models;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Services;
using Backend.Interfaces.Utilities;
using Microsoft.Extensions.Logging;

namespace Backend.Services;

public sealed class PluginDiscoveryService : IPluginDiscoveryService
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly IWorkflowTaskRepository _workflowTaskRepository;
    private readonly IPluginBlobNameParsingUtility _blobNameParser;
    private readonly ILogger<PluginDiscoveryService> _logger;

    public PluginDiscoveryService(
        BlobContainerClient blobContainerClient,
        IWorkflowTaskRepository workflowTaskRepository,
        IPluginBlobNameParsingUtility blobNameParser,
        ILogger<PluginDiscoveryService> logger)
    {
        _blobContainerClient = blobContainerClient;
        _workflowTaskRepository = workflowTaskRepository;
        _blobNameParser = blobNameParser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PluginBlobInfo>> GetNewPluginsAsync(CancellationToken cancellationToken)
    {
        var workflowTasks = (await _workflowTaskRepository.GetAll(cancellationToken))
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Version).ToHashSet());

        var pendingPlugins = new List<PluginBlobInfo>();

        await foreach (var blob in _blobContainerClient.GetBlobsAsync(
            new () { States = BlobStates.None, Traits = BlobTraits.None },
            cancellationToken: cancellationToken))
        {
            try
            {
                var pluginInfo = _blobNameParser.Parse(blob.Name);

                if (!workflowTasks.TryGetValue(pluginInfo.PluginName, out var versions) || !versions.Contains(pluginInfo.Version))
                {
                    pendingPlugins.Add(pluginInfo);
                }
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "Skipping blob {BlobName} due to invalid naming convention", blob.Name);
            }
        }

        return pendingPlugins;
    }
}
