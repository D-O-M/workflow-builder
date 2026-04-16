namespace Backend.Plugins.Harness;

internal abstract class PluginChannel
{
    public abstract string Name { get; }
    public abstract Type MessageType { get; }
    public abstract object? GetValue(object plugin);
}

internal sealed class PluginChannel<TPlugin, TChannelType> : PluginChannel
{
    public PluginChannel(string name, Func<TPlugin, TChannelType> getter)
    {
        Name = name;
        Getter = getter;
    }

    public Func<TPlugin, TChannelType> Getter { get; }
    public override string Name { get; }
    public override Type MessageType => typeof(TChannelType);

    public override object? GetValue(object plugin)
    {
        if (plugin is not TPlugin typedPlugin)
            throw new InvalidOperationException($"Plugin instance is not of expected type {typeof(TPlugin).FullName}.");

        return Getter(typedPlugin);
    }
}
