using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Services;
using Backend.Topology;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.PluginDetector;

public class PluginBlobDetectorFunction
{
    private readonly ILogger<PluginBlobDetectorFunction> _logger;
    private readonly IPluginBlobNameParsingUtility _blobNameParser;
    private readonly IPluginBundleProcessingService _bundleProcessor;
    private readonly IPluginIngestionStatusStore _pluginIngestionStatusStore;

    public PluginBlobDetectorFunction(
        ILogger<PluginBlobDetectorFunction> logger,
        IPluginBlobNameParsingUtility blobNameParser,
        IPluginBundleProcessingService bundleProcessor,
        IPluginIngestionStatusStore pluginIngestionStatusStore)
    {
        _logger = logger;
        _blobNameParser = blobNameParser;
        _bundleProcessor = bundleProcessor;
        _pluginIngestionStatusStore = pluginIngestionStatusStore;
    }

    [Function(nameof(PluginBlobDetectorFunction))]
    public async Task Run(
        [BlobTrigger(
            WorkflowBuilderTopology.Storage.BlobContainers.PluginsContainerNameBindingExpression + "/{name}",
            Connection = WorkflowBuilderTopology.Storage.AzureWebJobsStorageKey)]
        Stream stream,
        string name,
        CancellationToken cancellationToken)
    {
        _ = stream;
        _logger.LogInformation("Blob trigger received {BlobName}.", name);

        try
        {
            var pluginInfo = _blobNameParser.Parse(name);
            await RecordStatusAsync(
                PluginIngestionStatusUpdate.FromPlugin(
                    pluginInfo.BlobName,
                    pluginInfo.PluginName,
                    pluginInfo.Version,
                    PluginIngestionStatus.Detected,
                    "Plugin bundle blob detected."),
                cancellationToken);
            await _bundleProcessor.ProcessAsync(pluginInfo, cancellationToken);
        }
        catch (FormatException ex)
        {
            await RecordStatusAsync(
                new PluginIngestionStatusUpdate
                {
                    BlobName = name,
                    Status = PluginIngestionStatus.BundleNameInvalid,
                    Message = ex.Message
                },
                cancellationToken);
            await RecordStatusAsync(
                new PluginIngestionStatusUpdate
                {
                    BlobName = name,
                    Status = PluginIngestionStatus.Quarantined,
                    Message = ex.Message
                },
                cancellationToken);
            _logger.LogWarning(ex, "Skipping blob {BlobName} due to invalid naming convention.", name);
        }
    }

    private async Task RecordStatusAsync(
        PluginIngestionStatusUpdate update,
        CancellationToken cancellationToken)
    {
        try
        {
            await _pluginIngestionStatusStore.AppendEventAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Unable to record plugin ingestion status {Status} for blob {BlobName}.", update.Status, update.BlobName);
        }
    }
}
