using Backend.Engine.Capabilities;
using Backend.Services.EngineManagement;

namespace Backend.Engine;

internal static class TrustedCapabilityChecks
{
    private static readonly IReadOnlyDictionary<string, CapabilityRequirementDescriptor> Capabilities =
        typeof(EngineCapabilityAttribute).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(EngineCapabilityAttribute), inherit: false))
            .Select(CapabilityDescriptorReader.Describe)
            .ToDictionary(capability => capability.Id, StringComparer.Ordinal);

    public static IReadOnlyList<CapabilityRequirementDescriptor> Resolve(IEnumerable<CapabilityRequirementDescriptor> requested)
    {
        var checks = new List<CapabilityRequirementDescriptor>();
        foreach (var input in requested)
        {
            if (!Capabilities.TryGetValue(input.Id, out var definition) ||
                (input.Command is not null && input.Command != definition.Command) ||
                (input.Arguments is not null && input.Arguments != definition.Arguments) ||
                (input.EndpointConfigKey is not null && input.EndpointConfigKey != definition.EndpointConfigKey) ||
                input.ConfigurationKeys.Except(definition.ConfigurationKeys, StringComparer.Ordinal).Any() ||
                input.EngineConfigurationKeys.Except(definition.EngineConfigurationKeys, StringComparer.Ordinal).Any())
            {
                throw new InvalidDataException("The capability request does not match an installed Engine capability.");
            }
            checks.Add(definition);
        }
        return checks.DistinctBy(check => check.Id).ToArray();
    }

    public static ConfigurationRequirementDescriptor[] ConfigurationFor(
        IReadOnlyCollection<CapabilityRequirementDescriptor> capabilities, ManagementSettingsCatalog catalog)
    {
        var keys = capabilities.SelectMany(cap => cap.ConfigurationKeys).ToHashSet(StringComparer.Ordinal);
        return catalog.Fields.Where(field => keys.Contains(field.Key)).DistinctBy(field => field.Key)
            .Select(field => new ConfigurationRequirementDescriptor { Key = field.Key, Secret = field.Secret, Required = field.Required })
            .ToArray();
    }
}
