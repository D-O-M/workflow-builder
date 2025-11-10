namespace Plugins.Harness;

internal abstract class Channel
{
    public abstract Type MessageType { get; }
}

internal class Channel<TPlugin, TChannelType> : Channel
{
    public Channel(Func<TPlugin, TChannelType> targetProperty)
    {
        TargetProperty = targetProperty;
    }

    public Func<TPlugin, TChannelType> TargetProperty { get; init; }
    public override Type MessageType => typeof(TPlugin);
}
