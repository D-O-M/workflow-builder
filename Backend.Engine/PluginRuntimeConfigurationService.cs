using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backend.Interfaces.Utilities;
using Backend.Models;

namespace Backend.Engine;

public interface IPluginRuntimeConfigurationService
{
    Task<IReadOnlyList<FileInfo>> PrepareConfigurationFilesAsync(
        FileInfo pluginEntryPoint,
        string pluginDisplayName,
        CancellationToken cancellationToken);
}

public sealed class PluginRuntimeConfigurationService : IPluginRuntimeConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IConfiguration _configuration;
    private readonly IPluginManifestParsingUtility _manifestParser;
    private readonly ILogger<PluginRuntimeConfigurationService> _logger;

    public PluginRuntimeConfigurationService(
        IConfiguration configuration,
        IPluginManifestParsingUtility manifestParser,
        ILogger<PluginRuntimeConfigurationService> logger)
    {
        _configuration = configuration;
        _manifestParser = manifestParser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FileInfo>> PrepareConfigurationFilesAsync(
        FileInfo pluginEntryPoint,
        string pluginDisplayName,
        CancellationToken cancellationToken)
    {
        var workspaceDirectory = FindWorkspaceDirectory(pluginEntryPoint);
        var manifest = await _manifestParser.ReadAsync(workspaceDirectory.FullName, cancellationToken);
        if (manifest is null || manifest.Configuration.Requirements.Count == 0)
        {
            return [];
        }

        var resolvedByDomain = ResolveValues(pluginDisplayName, manifest.Configuration.Requirements);
        if (resolvedByDomain.Count == 0)
        {
            return [];
        }

        var root = GetConfigurationFileRoot();
        Directory.CreateDirectory(root);

        var files = new List<FileInfo>();
        foreach (var domain in resolvedByDomain.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var json = JsonSerializer.Serialize(domain.Value.Tree, JsonOptions);
            var hash = ComputeHash(json);
            var filePath = Path.Combine(root, $"{SanitizeFileName(domain.Key)}-{hash}.json");
            if (!File.Exists(filePath))
            {
                await File.WriteAllTextAsync(filePath, json, Encoding.UTF8, cancellationToken);
                ProtectFile(filePath);
            }

            files.Add(new FileInfo(filePath));
        }

        _logger.LogInformation(
            "Prepared {ConfigFileCount} plugin configuration file(s) for {PluginName}.",
            files.Count,
            pluginDisplayName);
        return files;
    }

    private Dictionary<string, ResolvedDomainConfiguration> ResolveValues(
        string pluginDisplayName,
        IReadOnlyCollection<ManifestFileConfigurationRequirementInfo> requirements)
    {
        var domains = new Dictionary<string, ResolvedDomainConfiguration>(StringComparer.Ordinal);
        foreach (var requirement in requirements.OrderBy(requirement => requirement.Key, StringComparer.Ordinal))
        {
            var section = _configuration.GetSection(requirement.Key);
            if (!section.Exists() || section.Value is null && !section.GetChildren().Any())
            {
                if (requirement.Required)
                {
                    throw new InvalidOperationException(
                        $"Required configuration value '{requirement.Key}' was not supplied for plugin {pluginDisplayName}.");
                }

                continue;
            }

            if (!domains.TryGetValue(requirement.DomainId, out var domain))
            {
                domain = new ResolvedDomainConfiguration(
                    new Dictionary<string, object?>(StringComparer.Ordinal));
                domains.Add(requirement.DomainId, domain);
            }

            CopySection(
                section,
                domain.Tree,
                requirement.Key.Split(':', StringSplitOptions.RemoveEmptyEntries));
        }

        return domains;
    }

    private static void CopySection(
        IConfigurationSection section,
        IDictionary<string, object?> target,
        IReadOnlyList<string> segments)
    {
        var current = target;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (index == segments.Count - 1)
            {
                current[segment] = section.Value is not null
                    ? section.Value
                    : CopyChildren(section);
                return;
            }

            if (!current.TryGetValue(segment, out var next) ||
                next is not IDictionary<string, object?> nextDictionary)
            {
                nextDictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                current[segment] = nextDictionary;
            }

            current = nextDictionary;
        }
    }

    private static Dictionary<string, object?> CopyChildren(IConfigurationSection section) =>
        section
            .GetChildren()
            .ToDictionary(
                child => child.Key,
                child => child.Value is not null ? (object?)child.Value : CopyChildren(child),
                StringComparer.Ordinal);

    private DirectoryInfo FindWorkspaceDirectory(FileInfo pluginEntryPoint)
    {
        var current = pluginEntryPoint.Directory;
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "manifest.json")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find manifest.json for plugin entry point '{pluginEntryPoint.FullName}'.");
    }

    private string GetConfigurationFileRoot() =>
        Path.GetFullPath(
            _configuration["PluginRuntime:ConfigurationFileRoot"]
            ?? Path.Combine(Path.GetTempPath(), "workflow-builder-plugin-config"));

    private static string ComputeHash(string json)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-');
        }

        return builder.Length == 0 ? "plugin-config" : builder.ToString();
    }

    private static void ProtectFile(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(filePath, File.GetAttributes(filePath) | FileAttributes.Temporary);
            return;
        }

        File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record ResolvedDomainConfiguration(Dictionary<string, object?> Tree);
}
