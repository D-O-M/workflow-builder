using Backend.Models;

namespace Backend.Interfaces.Services;

public interface IPluginDiscoveryService
{
    Task<IReadOnlyList<PluginBlobInfo>> GetNewPluginsAsync(
        CancellationToken cancellationToken);
}
