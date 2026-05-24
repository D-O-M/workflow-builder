using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Backend.Plugins.Harness;

public interface IPluginInputMaterializer<TInput>
    where TInput : class
{
    TInput Materialize(IReadOnlyDictionary<string, JsonElement> values);
}

public interface IPluginInputMaterializerProvider
{
    IPluginInputMaterializer<TInput>? GetMaterializer<TInput>()
        where TInput : class;
}

public static class PluginInputMaterializerUtilities
{
    public static bool TryGetValue(
        IReadOnlyDictionary<string, JsonElement> values,
        string propertyName,
        out JsonElement value)
    {
        if (values.TryGetValue(propertyName, out value))
        {
            return true;
        }

        foreach (var entry in values)
        {
            if (string.Equals(entry.Key, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string? ReadString(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    public static byte ReadByte(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? byte.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetByte();

    public static sbyte ReadSByte(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? sbyte.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetSByte();

    public static short ReadInt16(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? short.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetInt16();

    public static ushort ReadUInt16(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? ushort.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetUInt16();

    public static int ReadInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? int.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetInt32();

    public static uint ReadUInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? uint.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetUInt32();

    public static long ReadInt64(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? long.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetInt64();

    public static ulong ReadUInt64(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? ulong.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetUInt64();

    public static float ReadSingle(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? float.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetSingle();

    public static double ReadDouble(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? double.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetDouble();

    public static decimal ReadDecimal(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? decimal.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetDecimal();

    public static Guid ReadGuid(JsonElement element) => element.GetGuid();

    public static DateOnly ReadDateOnly(JsonElement element)
    {
        var value = element.GetString();
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        throw new JsonException($"Unable to read DateOnly value '{value}'.");
    }
}

internal static class PluginInputMaterialization
{
    public const string ProviderMetadataKey = "WorkflowBuilder.PluginInputMaterializerProvider";

    public static IPluginInputMaterializer<TInput>? ResolveMaterializer<TInput>(Type pluginType)
        where TInput : class
    {
        ArgumentNullException.ThrowIfNull(pluginType);

        var providerTypeName = pluginType.Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                ProviderMetadataKey,
                StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrWhiteSpace(providerTypeName))
        {
            return null;
        }

        var providerType = pluginType.Assembly.GetType(providerTypeName, throwOnError: false, ignoreCase: false)
            ?? throw new InvalidOperationException(
                $"Generated plugin input materializer provider '{providerTypeName}' could not be loaded from assembly '{pluginType.Assembly.FullName}'.");

        var defaultProperty = providerType.GetProperty(
            "Default",
            BindingFlags.Public | BindingFlags.Static);

        if (defaultProperty?.GetValue(null) is not IPluginInputMaterializerProvider provider)
        {
            throw new InvalidOperationException(
                $"Generated plugin input materializer provider '{providerType.FullName}' is missing a public static Default property.");
        }

        return provider.GetMaterializer<TInput>();
    }
}
