using Backend.Models;

namespace Backend.Interfaces.Utilities;

public interface IPluginBundleSource
{
    Task ExtractBundleAsync(
        PluginBlobInfo pluginInfo,
        string destinationDirectory,
        CancellationToken cancellationToken);
}
