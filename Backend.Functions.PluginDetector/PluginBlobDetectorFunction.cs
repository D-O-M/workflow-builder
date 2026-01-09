using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.PluginDetector;

public class PluginBlobDetectorFunction
{
    private readonly ILogger<PluginBlobDetectorFunction> _logger;
    private readonly IWorkflowTaskRepository _workflowTaskRepository;
    private readonly IPluginBlobNameParsingUtility _blobNameParser;
    private readonly IPluginBundleProcessingService _bundleProcessor;

    public PluginBlobDetectorFunction(
        ILogger<PluginBlobDetectorFunction> logger,
        IWorkflowTaskRepository workflowTaskRepository,
        IPluginBlobNameParsingUtility blobNameParser,
        IPluginBundleProcessingService bundleProcessor)
    {
        _logger = logger;
        _workflowTaskRepository = workflowTaskRepository;
        _blobNameParser = blobNameParser;
        _bundleProcessor = bundleProcessor;
    }

    [Function(nameof(PluginBlobDetectorFunction))]
    public async Task Run(
        [BlobTrigger("plugins/{name}", Connection = "plugin-blob")] Stream stream,
        string name,
        CancellationToken cancellationToken)
    {
        _ = stream;
        _logger.LogInformation("Blob trigger received {BlobName}.", name);

        try
        {
            var pluginInfo = _blobNameParser.Parse(name);
            var workflowTasks = await _workflowTaskRepository.GetAll(cancellationToken);
            var alreadyRegistered = workflowTasks.Any(task =>
                task.Name == pluginInfo.PluginName &&
                task.Version == pluginInfo.Version);

            if (alreadyRegistered)
            {
                _logger.LogInformation(
                    "Plugin {Plugin} ({Version}) already registered; skipping blob {BlobName}.",
                    pluginInfo.PluginName,
                    pluginInfo.Version,
                    pluginInfo.BlobName);
                return;
            }

            await _bundleProcessor.ProcessAsync(pluginInfo, cancellationToken);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Skipping blob {BlobName} due to invalid naming convention.", name);
        }
    }
}
