using Backend.Interfaces.Utilities;
using Microsoft.Extensions.Logging;

namespace Backend.Utilities;

public sealed class PluginWorkspaceFactoryUtility : IPluginWorkspaceFactoryUtility
{
    private readonly ILogger<PluginWorkspaceFactoryUtility> _logger;

    public PluginWorkspaceFactoryUtility(ILogger<PluginWorkspaceFactoryUtility> logger)
    {
        _logger = logger;
    }

    public Task<string> CreateWorkspaceAsync(string pluginName, Version pluginVersion, CancellationToken cancellationToken)
    {
        var sanitizedName = Sanitize(pluginName);
        var workDir = Path.Combine(Path.GetTempPath(), $"plugin_{sanitizedName}_{pluginVersion}");

        try
        {
            if (Directory.Exists(workDir))
            {
                Task.FromResult(workDir);
            }

            Directory.CreateDirectory(workDir);
            _logger.LogInformation("Created workspace {Workspace} for plugin {Plugin} ({Version})", workDir, pluginName, pluginVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare workspace {Workspace} for plugin {Plugin} ({Version})", workDir, pluginName, pluginVersion);
            throw;
        }

        return Task.FromResult(workDir);
    }

    private static string Sanitize(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(ch, '_');
        }

        return value;
    }
}
