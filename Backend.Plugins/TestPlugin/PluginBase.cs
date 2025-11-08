namespace TestPlugin;

public abstract class PluginBase
{
    public abstract Channel[] InputChannels { get; }
    public abstract Channel[] OutputChannels { get; }
}
