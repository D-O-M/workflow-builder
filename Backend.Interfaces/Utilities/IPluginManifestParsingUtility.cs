using Backend.Models;

namespace Backend.Interfaces.Utilities;

public interface IPluginManifestParsingUtility
{
    Task<ManifestFile?> ReadAsync(string workspaceDirectory,
        CancellationToken cancellationToken);
}
