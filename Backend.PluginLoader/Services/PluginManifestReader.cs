using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.PluginLoader.DTOs;
using Microsoft.Extensions.Logging;

namespace Backend.PluginLoader.Services;

internal interface IPluginManifestReader
{
    Task<ManifestFile?> ReadAsync(string workspaceDirectory, CancellationToken cancellationToken);
}

internal sealed class PluginManifestReader : IPluginManifestReader
{
    private readonly ILogger<PluginManifestReader> _logger;

    public PluginManifestReader(ILogger<PluginManifestReader> logger)
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
