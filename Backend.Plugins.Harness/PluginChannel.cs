namespace Backend.Plugins.Harness;

internal abstract class PluginChannel
{
    public abstract string Name { get; }
    public abstract Type MessageType { get; }
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
}
