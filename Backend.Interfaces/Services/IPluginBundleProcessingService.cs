using Backend.Models;

namespace Backend.Services;

public interface IPluginBundleProcessingService
{
    Task ProcessAsync(PluginBlobInfo pluginInfo, CancellationToken cancellationToken);
}
