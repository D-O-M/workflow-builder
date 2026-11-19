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
    Task<CapabilityValidationReport> ValidateAsync(
        PluginLaunchConfiguration settings, string pluginDisplayName, CancellationToken cancellationToken);
}

public sealed class PluginRuntimeCapabilityService : IPluginRuntimeCapabilityService
{
    private readonly IPluginManifestParsingUtility _manifestParser;
    private readonly ICapabilityValidationService _capabilityValidationService;
    private readonly ILogger<PluginRuntimeCapabilityService> _logger;
    private readonly ICommandCapabilityProbe _commandProbe;
    private readonly IEndpointCapabilityProbe _endpointProbe;

    public PluginRuntimeCapabilityService(
        IPluginManifestParsingUtility manifestParser,
        ICapabilityValidationService capabilityValidationService,
        ILogger<PluginRuntimeCapabilityService> logger,
        ICommandCapabilityProbe commandProbe, IEndpointCapabilityProbe endpointProbe)
    {
        _manifestParser = manifestParser;
        _capabilityValidationService = capabilityValidationService;
        _logger = logger;
        _commandProbe = commandProbe;
        _endpointProbe = endpointProbe;
    }

    public EngineCapabilityInventory GetInventory() =>
        _capabilityValidationService.GetInventory();

    public async Task<CapabilityValidationReport> ValidateWorkspaceAsync(
        FileInfo pluginEntryPoint,
        string pluginDisplayName,
        CancellationToken cancellationToken)
    {
        var manifest = await PluginLaunchConfiguration.ReadManifestAsync(pluginEntryPoint, _manifestParser, cancellationToken);
        return await ValidateManifestAsync(manifest, _capabilityValidationService, pluginDisplayName, null, cancellationToken);
    }

    public Task<CapabilityValidationReport> ValidateAsync(
        PluginLaunchConfiguration settings, string pluginDisplayName, CancellationToken cancellationToken) =>
        ValidateManifestAsync(settings.Manifest, new CapabilityValidationService(settings.Values, _commandProbe, _endpointProbe),
            pluginDisplayName, settings, cancellationToken);

    private async Task<CapabilityValidationReport> ValidateManifestAsync(
        ManifestFile? manifest, ICapabilityValidationService validator, string pluginDisplayName,
        PluginLaunchConfiguration? settings, CancellationToken cancellationToken)
    {
        if (manifest is null || manifest.Capabilities.Requirements.Count == 0)
        {
            return new CapabilityValidationReport();
        }

        var report = await validator.ValidateAsync(
            settings?.CapabilityChecks ?? manifest.Capabilities.Requirements.Select(MapCapability).ToArray(),
            settings?.ConfigurationChecks ?? manifest.Configuration.Requirements.Select(MapConfiguration).ToArray(),
            cancellationToken);

        if (settings is not null)
        {
            report = report with { Results = report.Results.Select(result => result with
            {
                Message = result.Message is null ? null : settings.Redact(result.Message),
                Remediation = result.Remediation is null ? null : settings.Redact(result.Remediation)
            }).ToArray() };
        }

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

    internal static CapabilityRequirementDescriptor MapCapability(
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
