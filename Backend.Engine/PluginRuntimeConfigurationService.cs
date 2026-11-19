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
    Task<IReadOnlyList<FileInfo>> WriteFilesAsync(
        PluginLaunchConfiguration settings, string pluginDisplayName, CancellationToken cancellationToken);
}

public sealed class PluginRuntimeConfigurationService : IPluginRuntimeConfigurationService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IConfiguration _configuration;
    private readonly IPluginManifestParsingUtility _manifestParser;
    private readonly ILogger<PluginRuntimeConfigurationService> _logger;
    private readonly PluginConfigurationWorkspace _workspace;

    public PluginRuntimeConfigurationService(
        IConfiguration configuration,
        IPluginManifestParsingUtility manifestParser,
        ILogger<PluginRuntimeConfigurationService> logger)
    {
        _configuration = configuration;
        _manifestParser = manifestParser;
        _logger = logger;
        _workspace = new PluginConfigurationWorkspace(GetConfigurationFileRoot());
    }

    public async Task<IReadOnlyList<FileInfo>> PrepareConfigurationFilesAsync(
        FileInfo pluginEntryPoint,
        string pluginDisplayName,
        CancellationToken cancellationToken)
    {
        var manifest = await PluginLaunchConfiguration.ReadManifestAsync(pluginEntryPoint, _manifestParser, cancellationToken);
        var settings = await PluginLaunchConfiguration.ResolveAsync(manifest, _configuration, null, null, cancellationToken);
        return await WriteFilesAsync(settings, pluginDisplayName, cancellationToken);
    }

    public async Task<IReadOnlyList<FileInfo>> WriteFilesAsync(
        PluginLaunchConfiguration settings, string pluginDisplayName, CancellationToken cancellationToken)
    {
        var manifest = settings.Manifest;
        if (manifest is null || manifest.Configuration.Requirements.Count == 0)
        {
            return [];
        }

        var resolvedByDomain = ResolveValues(settings.Values, pluginDisplayName, manifest.Configuration.Requirements);
        if (resolvedByDomain.Count == 0)
        {
            return [];
        }

        var directory = _workspace.CreateLaunchDirectory();
        var files = new List<FileInfo>();
        try
        {
            foreach (var domain in resolvedByDomain.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                var file = new FileInfo(Path.Combine(directory.FullName, $"{files.Count}.json"));
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var stream = file.Open(options);
                await JsonSerializer.SerializeAsync(stream, domain.Value.Tree, JsonOptions, cancellationToken);
                files.Add(file);
            }
        }
        catch
        {
            directory.Delete(recursive: true);
            throw;
        }

        _logger.LogInformation(
            "Prepared {ConfigFileCount} plugin configuration file(s) for {PluginName}.",
            files.Count,
            pluginDisplayName);
        return files;
    }

    private static Dictionary<string, ResolvedDomainConfiguration> ResolveValues(IConfiguration configuration,
        string pluginDisplayName,
        IReadOnlyCollection<ManifestFileConfigurationRequirementInfo> requirements)
    {
        var domains = new Dictionary<string, ResolvedDomainConfiguration>(StringComparer.Ordinal);
        foreach (var requirement in requirements.OrderBy(requirement => requirement.Key, StringComparer.Ordinal))
        {
            var section = configuration.GetSection(requirement.Key);
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

    private string GetConfigurationFileRoot() =>
        Path.GetFullPath(
            _configuration["PluginRuntime:ConfigurationFileRoot"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify), "WorkflowBuilder", "PluginRuntime"));

    public void Dispose() => _workspace.Dispose();

    private sealed record ResolvedDomainConfiguration(Dictionary<string, object?> Tree);
}
