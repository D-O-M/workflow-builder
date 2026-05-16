using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Backend.Engine.Capabilities;
using Backend.Plugins.Configuration;

namespace Backend.Plugins.Harness;

internal static class PluginConfigurationHelpWriter
{
    private static readonly NullabilityInfoContext NullabilityContext = new();

    public static bool ShouldShowHelp(IReadOnlyList<string> args) =>
        args.Any(arg =>
            string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase));

    public static async Task WriteAsync(
        TextWriter writer,
        string displayName,
        IReadOnlyCollection<Type> capabilityTypes)
    {
        await writer.WriteLineAsync(displayName);
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("Usage:");
        await writer.WriteLineAsync("  plugin --config <domain-config-file> [--config <domain-config-file>]");

        if (capabilityTypes.Count == 0)
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("Capabilities:");
            await writer.WriteLineAsync("  No plugin-specific capabilities.");
        }
        else
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("Capabilities:");
            foreach (var capability in capabilityTypes
                         .Select(CapabilityDescriptorReader.Describe)
                         .OrderBy(requirement => requirement.Id, StringComparer.Ordinal))
            {
                await writer.WriteLineAsync(
                    $"  {capability.Id} ({capability.Kind}, {(capability.Required ? "required" : "optional")})");
                if (!string.IsNullOrWhiteSpace(capability.Description))
                {
                    await writer.WriteLineAsync($"    {capability.Description}");
                }
            }
        }

        var configurationTypes = capabilityTypes
            .SelectMany(CapabilityDescriptorReader.GetConfigurationTypes)
            .Distinct()
            .ToArray();

        if (configurationTypes.Length == 0)
        {
            await writer.WriteLineAsync();
            await writer.WriteLineAsync("Configuration:");
            await writer.WriteLineAsync("  No plugin-specific configuration.");
            return;
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync("Configuration:");
        foreach (var requirement in configurationTypes
                     .OrderBy(PluginConfigurationKey.GetDomainId, StringComparer.Ordinal)
                     .ThenBy(PluginConfigurationKey.GetOptionsSectionName, StringComparer.Ordinal)
                     .SelectMany(BuildRequirements)
                     .OrderBy(requirement => requirement.Key, StringComparer.Ordinal))
        {
            var requiredText = requirement.Required ? "required" : "optional";
            var secretText = requirement.Secret ? ", secret" : "";
            await writer.WriteLineAsync(
                $"  {requirement.Key} ({requirement.ClrType}, {requiredText}{secretText})");
            if (!string.IsNullOrWhiteSpace(requirement.Description))
            {
                await writer.WriteLineAsync($"    {requirement.Description}");
            }
        }
    }

    private static IEnumerable<ConfigurationRequirement> BuildRequirements(Type optionsType)
    {
        var domainId = PluginConfigurationKey.GetDomainId(optionsType);
        var optionsName = PluginConfigurationKey.GetOptionsSectionName(optionsType);
        return BuildRequirements(optionsType, domainId, optionsName, []);
    }

    private static IEnumerable<ConfigurationRequirement> BuildRequirements(
        Type type,
        string domainId,
        string optionsName,
        IReadOnlyList<string> propertyPath)
    {
        foreach (var property in type
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.GetIndexParameters().Length == 0 &&
                                        property.SetMethod is not null &&
                                        property.SetMethod.IsPublic)
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            var nextPath = propertyPath.Concat([property.Name]).ToArray();
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var isCollection = propertyType != typeof(string) &&
                               typeof(IEnumerable).IsAssignableFrom(propertyType);
            var isSecret = TryGetSecretValueType(propertyType, out var secretValueType);
            var valueType = isSecret ? secretValueType : propertyType;

            if (!isCollection && !isSecret && IsNestedObject(valueType))
            {
                foreach (var nestedRequirement in BuildRequirements(valueType, domainId, optionsName, nextPath))
                {
                    yield return nestedRequirement;
                }

                continue;
            }

            yield return new ConfigurationRequirement(
                $"{domainId}:{optionsName}:{string.Join(":", nextPath)}",
                GetFriendlyTypeName(valueType, isCollection),
                IsRequired(property),
                isSecret,
                GetDescription(property));
        }
    }

    private static bool IsNestedObject(Type type) =>
        type.IsClass &&
        type != typeof(string) &&
        type != typeof(Uri) &&
        type != typeof(DateOnly) &&
        type != typeof(DateTime) &&
        type != typeof(DateTimeOffset) &&
        type != typeof(TimeSpan);

    private static bool IsRequired(PropertyInfo property)
    {
        if (property.GetCustomAttribute<RequiredMemberAttribute>() is not null)
        {
            return true;
        }

        var propertyType = property.PropertyType;
        if (propertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(propertyType) is null;
        }

        return NullabilityContext.Create(property).WriteState == NullabilityState.NotNull;
    }

    private static string GetFriendlyTypeName(Type type, bool isCollection)
    {
        var valueType = isCollection
            ? GetCollectionElementType(type) ?? type
            : type;
        var name = valueType == typeof(string) ? "string" : valueType.Name;
        return isCollection ? $"{name}[]" : name;
    }

    private static Type? GetCollectionElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type
            .GetInterfaces()
            .Concat([type])
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    private static bool TryGetSecretValueType(Type type, out Type valueType)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Secret<>))
        {
            valueType = type.GetGenericArguments()[0];
            return true;
        }

        valueType = type;
        return false;
    }

    private static string? GetDescription(PropertyInfo property) =>
        property.GetCustomAttribute<DescriptionAttribute>()?.Description;

    private sealed record ConfigurationRequirement(
        string Key,
        string ClrType,
        bool Required,
        bool Secret,
        string? Description);
}
