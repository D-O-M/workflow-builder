using Backend.Engine.Capabilities;
using Backend.Interfaces.Utilities;
using Backend.Models;

namespace Backend.Engine;

public interface IPluginRuntimeCapabilityService
{
    Task<CapabilityValidationReport> ValidateWorkspaceAsync(
        FileInfo pluginEntryPoint,
        string pluginDisplayName,
        CancellationToken cancellationToken);

    EngineCapabilityInventory GetInventory();
}

public sealed class PluginRuntimeCapabilityService : IPluginRuntimeCapabilityService
{
    private readonly IPluginManifestParsingUtility _manifestParser;
    private readonly ICapabilityValidationService _capabilityValidationService;
    private readonly ILogger<PluginRuntimeCapabilityService> _logger;

    public PluginRuntimeCapabilityService(
        IPluginManifestParsingUtility manifestParser,
        ICapabilityValidationService capabilityValidationService,
        ILogger<PluginRuntimeCapabilityService> logger)
    {
        _manifestParser = manifestParser;
        _capabilityValidationService = capabilityValidationService;
        _logger = logger;
    }

    public EngineCapabilityInventory GetInventory() =>
        _capabilityValidationService.GetInventory();

    public async Task<CapabilityValidationReport> ValidateWorkspaceAsync(
        FileInfo pluginEntryPoint,
        string pluginDisplayName,
        CancellationToken cancellationToken)
    {
        var workspaceDirectory = FindWorkspaceDirectory(pluginEntryPoint);
        var manifest = await _manifestParser.ReadAsync(workspaceDirectory.FullName, cancellationToken);
        if (manifest is null || manifest.Capabilities.Requirements.Count == 0)
        {
            return new CapabilityValidationReport();
        }

        var report = await _capabilityValidationService.ValidateAsync(
            manifest.Capabilities.Requirements.Select(MapCapability).ToArray(),
            manifest.Configuration.Requirements.Select(MapConfiguration).ToArray(),
            cancellationToken);

        foreach (var failure in report.Results.Where(result => result.Status != CapabilityValidationStatus.Satisfied))
        {
            _logger.LogWarning(
                "Plugin {PluginName} is missing capability {CapabilityId} ({CapabilityKind}): {Message} Remediation: {Remediation}",
                pluginDisplayName,
                failure.CapabilityId,
                failure.Kind,
                failure.Message,
                failure.Remediation ?? "<none>");
        }

        return report;
    }

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

    private static CapabilityRequirementDescriptor MapCapability(
        ManifestFileCapabilityRequirementInfo requirement) =>
        new()
        {
            Id = requirement.Id,
            Kind = requirement.Kind,
            CapabilityType = requirement.CapabilityType,
            DisplayName = requirement.DisplayName,
            Description = requirement.Description,
            Required = requirement.Required,
            Installable = requirement.Installable,
            Remediation = requirement.Remediation,
            Command = requirement.Command,
            Arguments = requirement.Arguments,
            MinimumVersion = requirement.MinimumVersion,
            EndpointConfigKey = requirement.EndpointConfigKey,
            TimeoutSeconds = requirement.TimeoutSeconds,
            ConfigurationKeys = requirement.ConfigurationKeys,
            EngineConfigurationKeys = requirement.EngineConfigurationKeys
        };

    private static ConfigurationRequirementDescriptor MapConfiguration(
        ManifestFileConfigurationRequirementInfo requirement) =>
        new()
        {
            Key = requirement.Key,
            Required = requirement.Required,
            Secret = requirement.Secret
        };
}
