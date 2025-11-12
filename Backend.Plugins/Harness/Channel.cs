namespace Plugins.Harness;

internal abstract class Channel
{
    public abstract string Name { get; }
    public abstract Type MessageType { get; }
}

internal sealed class Channel<TPlugin, TChannelType> : Channel
{
    public Channel(string name, Func<TPlugin, TChannelType> getter)
    {
        Name = name;
        Getter = getter;
    }

    public Func<TPlugin, TChannelType> Getter { get; }
    public override string Name { get; }
    public override Type MessageType => typeof(TChannelType);
}
