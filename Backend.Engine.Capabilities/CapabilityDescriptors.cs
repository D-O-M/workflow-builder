using Backend.Plugins.Configuration;

namespace Backend.Engine.Capabilities;

public enum CapabilityValidationStatus
{
    Satisfied,
    Missing,
    Failed,
    Unsupported
}

public sealed record CapabilityRequirementDescriptor
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string CapabilityType { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Description { get; init; }
    public bool Required { get; init; } = true;
    public bool Installable { get; init; }
    public string? Remediation { get; init; }
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    public string? MinimumVersion { get; init; }
    public string? EndpointConfigKey { get; init; }
    public int? TimeoutSeconds { get; init; }
    public IReadOnlyList<string> ConfigurationKeys { get; init; } = [];
    public IReadOnlyList<string> EngineConfigurationKeys { get; init; } = [];
}

public sealed record ConfigurationRequirementDescriptor
{
    public string Key { get; init; } = "";
    public bool Required { get; init; }
    public bool Secret { get; init; }
}

public sealed record CapabilityValidationResult
{
    public string CapabilityId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Kind { get; init; } = "";
    public bool Required { get; init; } = true;
    public CapabilityValidationStatus Status { get; init; }
    public string? Message { get; init; }
    public string? Remediation { get; init; }
}

public sealed record CapabilityValidationReport
{
    public IReadOnlyList<CapabilityValidationResult> Results { get; init; } = [];
    public bool Succeeded => Results.All(result =>
        result.Status == CapabilityValidationStatus.Satisfied ||
        !result.Required);
}

public sealed record EngineCapabilityInventory
{
    public IReadOnlyList<string> SupportedKinds { get; init; } =
    [
        CapabilityKindNames.Software,
        CapabilityKindNames.Network,
        CapabilityKindNames.Configuration
    ];

    public IReadOnlyList<string> SupportedValidators { get; init; } =
    [
        "command",
        "http-endpoint",
        "configuration"
    ];
}

public static class CapabilityDescriptorReader
{
    public const string CapabilityNamespacePrefix = "Backend.Engine.Capabilities";

    public static CapabilityRequirementDescriptor Describe(Type capabilityType)
    {
        if (!IsApprovedCapabilityType(capabilityType))
        {
            throw new InvalidOperationException(
                $"Capability type '{capabilityType.FullName}' must live under '{CapabilityNamespacePrefix}'.");
        }

        var capability = capabilityType
            .GetCustomAttributes(typeof(EngineCapabilityAttribute), inherit: false)
            .Cast<EngineCapabilityAttribute>()
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Capability type '{capabilityType.FullName}' is missing {nameof(EngineCapabilityAttribute)}.");

        var command = capabilityType
            .GetCustomAttributes(typeof(CommandCapabilityAttribute), inherit: false)
            .Cast<CommandCapabilityAttribute>()
            .SingleOrDefault();
        var endpoint = capabilityType
            .GetCustomAttributes(typeof(HttpEndpointCapabilityAttribute), inherit: false)
            .Cast<HttpEndpointCapabilityAttribute>()
            .SingleOrDefault();

        return new CapabilityRequirementDescriptor
        {
            Id = capability.Id,
            Kind = capability.Kind,
            CapabilityType = capabilityType.FullName ?? capabilityType.Name,
            DisplayName = capability.DisplayName,
            Description = capability.Description,
            Required = capability.Required,
            Installable = capability.Installable,
            Remediation = capability.Remediation,
            Command = command?.Command,
            Arguments = command?.Arguments,
            MinimumVersion = command?.MinimumVersion,
            EndpointConfigKey = endpoint?.EndpointConfigKey,
            TimeoutSeconds = endpoint?.TimeoutSeconds,
            ConfigurationKeys = GetConfigurationTypes(capabilityType)
                .SelectMany(GetConfigurationKeys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            EngineConfigurationKeys = capabilityType
                .GetCustomAttributes(typeof(EngineConfigurationKeyAttribute), inherit: false)
                .Cast<EngineConfigurationKeyAttribute>()
                .Select(attribute => attribute.Key)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
    }

    public static IReadOnlyList<Type> GetConfigurationTypes(Type capabilityType) =>
        capabilityType
            .GetCustomAttributes(typeof(RequiresConfigurationAttribute), inherit: false)
            .Cast<RequiresConfigurationAttribute>()
            .Select(attribute => attribute.OptionsType)
            .Distinct()
            .ToArray();

    public static bool IsApprovedCapabilityType(Type capabilityType) =>
        capabilityType.Namespace?.StartsWith(CapabilityNamespacePrefix, StringComparison.Ordinal) == true;

    private static IEnumerable<string> GetConfigurationKeys(Type optionsType)
    {
        var domainId = PluginConfigurationKey.GetDomainId(optionsType);
        var optionsName = PluginConfigurationKey.GetOptionsSectionName(optionsType);
        return GetConfigurationKeys(optionsType, domainId, optionsName, []);
    }

    private static IEnumerable<string> GetConfigurationKeys(
        Type type,
        string domainId,
        string optionsName,
        IReadOnlyList<string> propertyPath)
    {
        foreach (var property in type
                     .GetProperties()
                     .Where(property => property.GetIndexParameters().Length == 0 &&
                                        property.SetMethod is not null &&
                                        property.SetMethod.IsPublic)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            var nextPath = propertyPath.Concat([property.Name]).ToArray();
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!IsLeafConfigurationType(propertyType) &&
                !IsSecret(propertyType) &&
                !IsCollection(propertyType))
            {
                foreach (var key in GetConfigurationKeys(propertyType, domainId, optionsName, nextPath))
                {
                    yield return key;
                }

                continue;
            }

            yield return $"{domainId}:{optionsName}:{string.Join(":", nextPath)}";
        }
    }

    private static bool IsLeafConfigurationType(Type type) =>
        type.IsEnum ||
        type == typeof(string) ||
        type == typeof(bool) ||
        type == typeof(byte) ||
        type == typeof(short) ||
        type == typeof(int) ||
        type == typeof(long) ||
        type == typeof(float) ||
        type == typeof(double) ||
        type == typeof(decimal) ||
        type == typeof(Guid) ||
        type == typeof(Uri) ||
        type == typeof(DateOnly) ||
        type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan);

    private static bool IsSecret(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Secret<>);

    private static bool IsCollection(Type type) =>
        type != typeof(string) && type.GetInterfaces().Concat([type]).Any(candidate =>
            candidate.IsGenericType &&
            candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
}
