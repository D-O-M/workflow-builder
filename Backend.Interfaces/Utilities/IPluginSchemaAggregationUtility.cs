using Backend.Models;

namespace Backend.Interfaces.Utilities;

public interface IPluginSchemaAggregationUtility
{
    Task<PluginSchemaResult?> LoadAsync(
        string workspaceDirectory,
        CancellationToken cancellationToken);
}
