namespace Backend.Plugins.Harness;

internal sealed class PluginChannel
{
    public PluginChannel(string name, Type messageType)
    {
        Name = name;
        MessageType = messageType;
    }

    public string Name { get; }
    public Type MessageType { get; }
}
