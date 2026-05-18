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

    public PluginBlobDetectorFunction(
        ILogger<PluginBlobDetectorFunction> logger,
        IPluginBlobNameParsingUtility blobNameParser,
        IPluginBundleProcessingService bundleProcessor)
    {
        _logger = logger;
        _blobNameParser = blobNameParser;
        _bundleProcessor = bundleProcessor;
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
            await _bundleProcessor.ProcessAsync(pluginInfo, cancellationToken);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Skipping blob {BlobName} due to invalid naming convention.", name);
        }
    }
}
