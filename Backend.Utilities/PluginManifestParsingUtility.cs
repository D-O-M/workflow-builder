using System.Text.Json;
using Backend.Models;
using Backend.Interfaces.Utilities;
using Microsoft.Extensions.Logging;

namespace Backend.Utilities;

public sealed class PluginManifestParsingUtility : IPluginManifestParsingUtility
{
    private readonly ILogger<PluginManifestParsingUtility> _logger;

    public PluginManifestParsingUtility(ILogger<PluginManifestParsingUtility> logger)
    {
        _logger = logger;
    }

    public async Task<ManifestFile?> ReadAsync(string workspaceDirectory, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(workspaceDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            _logger.LogWarning("No manifest.json found in workspace {Workspace}", workspaceDirectory);
            return null;
        }

        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ManifestFile>(
            manifestStream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);

        if (manifest is null)
        {
            _logger.LogWarning("Unable to parse manifest at {ManifestPath}", manifestPath);
        }

        return manifest;
    }
}
