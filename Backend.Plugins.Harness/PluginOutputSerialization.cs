using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Backend.Plugins.Harness;

internal interface IPluginOutputChannel<TPlugin>
    where TPlugin : class, IPlugin
{
    string Name { get; }
    JsonElement? Serialize(TPlugin plugin);
}

internal interface IPluginGlobalStateSerializer<TPlugin>
    where TPlugin : class, IPlugin
{
    Dictionary<string, JsonElement> Serialize(TPlugin plugin);
}

internal sealed class PluginOutputChannel<TPlugin, TChannelType> : IPluginOutputChannel<TPlugin>
    where TPlugin : class, IPlugin
    where TChannelType : class
{
    private readonly Func<TPlugin, TChannelType> _getter;
    private readonly JsonTypeInfo<TChannelType>? _jsonTypeInfo;

    public PluginOutputChannel(
        string name,
        Func<TPlugin, TChannelType> getter,
        JsonTypeInfo<TChannelType>? jsonTypeInfo)
    {
        Name = name;
        _getter = getter;
        _jsonTypeInfo = jsonTypeInfo;
    }

    public string Name { get; }

    public JsonElement? Serialize(TPlugin plugin)
    {
        var value = _getter(plugin);
        if (value is null)
        {
            return null;
        }

        return _jsonTypeInfo is null
            ? JsonSerializer.SerializeToElement(value, PluginJsonSerialization.ContractJsonOptions)
            : JsonSerializer.SerializeToElement(value, _jsonTypeInfo);
    }
}

internal sealed class PluginGlobalStateSerializer<TPlugin, TGlobalState> : IPluginGlobalStateSerializer<TPlugin>
    where TPlugin : class, IPlugin
    where TGlobalState : class, IGlobalState
{
    private readonly Func<TPlugin, TGlobalState> _getter;
    private readonly JsonTypeInfo<TGlobalState>? _jsonTypeInfo;

    public PluginGlobalStateSerializer(
        Func<TPlugin, TGlobalState> getter,
        JsonTypeInfo<TGlobalState>? jsonTypeInfo)
    {
        _getter = getter;
        _jsonTypeInfo = jsonTypeInfo;
    }

    public Dictionary<string, JsonElement> Serialize(TPlugin plugin)
    {
        var value = _getter(plugin);
        if (value is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var element = _jsonTypeInfo is null
            ? JsonSerializer.SerializeToElement(value, PluginJsonSerialization.ContractJsonOptions)
            : JsonSerializer.SerializeToElement(value, _jsonTypeInfo);

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        return element
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);
    }
}

internal static class PluginJsonSerialization
{
    public const string ContextMetadataKey = "WorkflowBuilder.PluginJsonSerializerContext";

    public static readonly JsonSerializerOptions ContractJsonOptions = new(JsonSerializerDefaults.General);
    public static readonly JsonSerializerOptions InputJsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonSerializerContext? ResolveContext(Type pluginType)
    {
        ArgumentNullException.ThrowIfNull(pluginType);

        var contextTypeName = pluginType.Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                ContextMetadataKey,
                StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrWhiteSpace(contextTypeName))
        {
            return null;
        }

        var contextType = pluginType.Assembly.GetType(contextTypeName, throwOnError: false, ignoreCase: false)
            ?? throw new InvalidOperationException(
                $"Generated plugin JSON serializer context '{contextTypeName}' could not be loaded from assembly '{pluginType.Assembly.FullName}'.");

        var defaultProperty = contextType.GetProperty(
            "Default",
            BindingFlags.Public | BindingFlags.Static);

        if (defaultProperty?.GetValue(null) is not JsonSerializerContext context)
        {
            throw new InvalidOperationException(
                $"Generated plugin JSON serializer context '{contextType.FullName}' is missing a public static Default property.");
        }

        return context;
    }

    public static JsonTypeInfo<TContract>? GetTypeInfo<TContract>(
        JsonSerializerContext? context,
        string contractDescription)
    {
        if (context is null)
        {
            return null;
        }

        return GetTypeInfoCore<TContract>(context, contractDescription);
    }

    public static JsonTypeInfo<TContract> RequireTypeInfo<TContract>(
        JsonSerializerContext? context,
        string contractDescription)
    {
        if (context is null)
        {
            throw new InvalidOperationException(
                $"Plugin assembly does not declare a generated JSON serializer context. Add [PluginJsonSerializable] to the {contractDescription} DTO so the plugin harness can use source-generated JSON metadata.");
        }

        return GetTypeInfoCore<TContract>(context, contractDescription);
    }

    private static JsonTypeInfo<TContract> GetTypeInfoCore<TContract>(
        JsonSerializerContext context,
        string contractDescription)
    {
        var typeInfo = context.GetTypeInfo(typeof(TContract));
        if (typeInfo is JsonTypeInfo<TContract> typedTypeInfo)
        {
            return typedTypeInfo;
        }

        throw new InvalidOperationException(
            $"Generated plugin JSON serializer context '{context.GetType().FullName}' does not include {contractDescription} type '{typeof(TContract).FullName}'. Add [PluginJsonSerializable] to the DTO.");
    }
}
