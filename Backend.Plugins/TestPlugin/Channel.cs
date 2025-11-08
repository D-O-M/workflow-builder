namespace TestPlugin;

public abstract class Channel
{
    public string Description { get; }

    protected Channel(string description) => Description = description;

    public abstract Type MessageType { get; }
}

public sealed class Channel<T> : Channel
{
    public Channel(string description) : base(description) { }
    public override Type MessageType => typeof(T);
}
