using System.Text.Json;

namespace Backend.Plugins.Harness;

internal sealed class PluginStateConfiguration<TPlugin>
    where TPlugin : class, IPlugin
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public PluginStateBinding<TPlugin>? GlobalState { get; private set; }

    public Dictionary<string, PluginStateBinding<TPlugin>> InputChannels { get; } =
        new(StringComparer.Ordinal);

    public bool HasBindings => GlobalState is not null || InputChannels.Count > 0;

    public void SetGlobalState<TState>(Action<TPlugin, TState> setter)
        where TState : class
    {
        GlobalState = PluginStateBinding<TPlugin>.Create(setter, SerializerOptions);
    }

    public void AddInputChannel<TState>(string name, Action<TPlugin, TState> setter)
    {
        InputChannels[name] = PluginStateBinding<TPlugin>.Create(setter, SerializerOptions);
    }
}

internal sealed class PluginStateBinding<TPlugin>
    where TPlugin : class, IPlugin
{
    private readonly Action<TPlugin, object> _setter;
    private readonly Func<ReadOnlyMemory<byte>, object> _deserialize;

    private PluginStateBinding(
        Action<TPlugin, object> setter,
        Func<ReadOnlyMemory<byte>, object> deserialize)
    {
        _setter = setter;
        _deserialize = deserialize;
    }

    public Action<TPlugin, object> Setter => _setter;

    public object Deserialize(ReadOnlyMemory<byte> data) => _deserialize(data);

    public static PluginStateBinding<TPlugin> Create<TState>(
        Action<TPlugin, TState> setter,
        JsonSerializerOptions options)
        where TState : class
    {
        return new PluginStateBinding<TPlugin>(
            (plugin, value) => setter(plugin, (TState)value),
            data => JsonSerializer.Deserialize<TState>(data.Span, options)
                ?? throw new InvalidOperationException("Unable to deserialize state payload."));
    }
}
