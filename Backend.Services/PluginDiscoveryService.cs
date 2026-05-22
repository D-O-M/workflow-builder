using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Backend.Database.State.Objects;
using Backend.Models;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Services;
using Backend.Interfaces.Utilities;
using Microsoft.Extensions.Logging;

namespace Backend.Services;

public sealed class PluginDiscoveryService : IPluginDiscoveryService
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly ITaskDefinitionRepository _taskDefinitionRepository;
    private readonly IPluginBlobNameParsingUtility _blobNameParser;
    private readonly IPluginIngestionStatusStore _pluginIngestionStatusStore;
    private readonly ILogger<PluginDiscoveryService> _logger;

    public PluginDiscoveryService(
        BlobContainerClient blobContainerClient,
        ITaskDefinitionRepository taskDefinitionRepository,
        IPluginBlobNameParsingUtility blobNameParser,
        IPluginIngestionStatusStore pluginIngestionStatusStore,
        ILogger<PluginDiscoveryService> logger)
    {
        _blobContainerClient = blobContainerClient;
        _taskDefinitionRepository = taskDefinitionRepository;
        _blobNameParser = blobNameParser;
        _pluginIngestionStatusStore = pluginIngestionStatusStore;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PluginBlobInfo>> GetNewPluginsAsync(CancellationToken cancellationToken)
    {
        var registeredTaskDefinitions = (await _taskDefinitionRepository.GetAll(cancellationToken))
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

                if (!registeredTaskDefinitions.TryGetValue(pluginInfo.PluginName, out var versions) || !versions.Contains(pluginInfo.Version))
                {
                    pendingPlugins.Add(pluginInfo);
                }
            }
            catch (FormatException ex)
            {
                await RecordInvalidBlobNameAsync(blob.Name, ex, cancellationToken);
                _logger.LogWarning(ex, "Skipping blob {BlobName} due to invalid naming convention", blob.Name);
            }
        }

        return pendingPlugins;
    }

    private async Task RecordInvalidBlobNameAsync(
        string blobName,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _pluginIngestionStatusStore.AppendEventAsync(
                new PluginIngestionStatusUpdate
                {
                    BlobName = blobName,
                    Status = PluginIngestionStatus.BundleNameInvalid,
                    Message = exception.Message
                },
                cancellationToken);
            await _pluginIngestionStatusStore.AppendEventAsync(
                new PluginIngestionStatusUpdate
                {
                    BlobName = blobName,
                    Status = PluginIngestionStatus.Quarantined,
                    Message = exception.Message
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Unable to record invalid plugin blob name status for {BlobName}.", blobName);
        }
    }
}
