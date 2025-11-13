using Backend.DTOs;

namespace Backend.Interfaces.Services;

public interface IPluginDiscoveryService
{
    Task<IReadOnlyList<PluginBlobInfo>> GetNewPluginsAsync(
        CancellationToken cancellationToken);
}
