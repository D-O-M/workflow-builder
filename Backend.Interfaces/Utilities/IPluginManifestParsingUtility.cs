using Backend.DTOs;

namespace Backend.Interfaces.Utilities;

public interface IPluginManifestParsingUtility
{
    Task<ManifestFile?> ReadAsync(string workspaceDirectory,
        CancellationToken cancellationToken);
}
