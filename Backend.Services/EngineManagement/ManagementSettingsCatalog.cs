using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using Backend.Engine.Capabilities;
using Backend.Plugins.Configuration;

namespace Backend.Services.EngineManagement;

public sealed record ManagementSettingDefinition(string Key, string CapabilityId, string CapabilityName,
    string Kind, string? Description, bool Required, bool Secret, Type ValueType)
{
    public bool Accepts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 25 * 1024)
            return false;
        if (ValueType == typeof(Uri))
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo);
        if (ValueType == typeof(string)) return true;
        if (ValueType.IsEnum) return Enum.TryParse(ValueType, value, true, out var parsed) && Enum.IsDefined(ValueType, parsed!);
        try
        {
            return TypeDescriptor.GetConverter(ValueType).ConvertFrom(null, CultureInfo.InvariantCulture, value) is not null;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or OverflowException)
        {
            return false;
        }
    }
}

public sealed class ManagementSettingsCatalog
{
    public IReadOnlyList<ManagementSettingDefinition> Fields { get; } = Discover();

    public ManagementSettingDefinition? Find(string key) =>
        Fields.FirstOrDefault(field => field.Key == key);

    private static IReadOnlyList<ManagementSettingDefinition> Discover()
    {
        var fields = new List<ManagementSettingDefinition>();
        var nullability = new NullabilityInfoContext();
        foreach (var capabilityType in typeof(EngineCapabilityAttribute).Assembly.GetTypes()
                     .Where(t => t.IsDefined(typeof(EngineCapabilityAttribute), false)))
        {
            var capability = CapabilityDescriptorReader.Describe(capabilityType);
            foreach (var options in CapabilityDescriptorReader.GetConfigurationTypes(capabilityType))
            {
                var prefix = PluginConfigurationKey.GetSectionPath(options) + ":";
                foreach (var key in capability.ConfigurationKeys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    var type = options;
                    PropertyInfo? property = null;
                    foreach (var segment in key[prefix.Length..].Split(':'))
                    {
                        property = type.GetProperty(segment) ?? throw new InvalidOperationException("Invalid configuration declaration.");
                        type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                    }
                    var secret = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Secret<>);
                    if (secret) type = type.GetGenericArguments()[0];
                    fields.Add(new(key, capability.Id, capability.DisplayName, type.Name,
                        property!.GetCustomAttribute<DescriptionAttribute>()?.Description,
                        nullability.Create(property!).ReadState == NullabilityState.NotNull,
                        secret, type));
                }
            }
            // Engine-only declarations carry no public value schema. Protect their values by default.
            foreach (var key in capabilityType.GetCustomAttributes<EngineConfigurationKeyAttribute>())
                fields.Add(new(key.Key, capability.Id, capability.DisplayName, "String", capability.Description,
                    key.Required, true, typeof(string)));
        }
        foreach (var group in fields.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            if (group.Select(f => (f.Key, f.Secret, f.ValueType)).Distinct().Count() > 1)
                throw new InvalidOperationException($"Conflicting management declarations for '{group.Key}'.");
        return fields.OrderBy(f => f.CapabilityName).ThenBy(f => f.Key).ToArray();
    }
}
