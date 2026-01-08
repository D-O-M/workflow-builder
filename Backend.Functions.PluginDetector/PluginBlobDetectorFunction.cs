using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.PluginDetector;

public class PluginBlobDetectorFunction
{
    private readonly ILogger<PluginBlobDetectorFunction> _logger;

    public PluginBlobDetectorFunction(ILogger<PluginBlobDetectorFunction> logger)
    {
        _logger = logger;
    }

    [Function(nameof(PluginBlobDetectorFunction))]
    public async Task Run([BlobTrigger("plugins/{name}", Connection = "plugin-blob")] Stream stream, string name)
    {
        using var blobStreamReader = new StreamReader(stream);
        var content = await blobStreamReader.ReadToEndAsync();
        _logger.LogInformation($"C# Blob trigger function Processed blob\n Name: {name} \n Data: {content}");
    }
}
