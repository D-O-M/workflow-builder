namespace Plugins.Harness;

public sealed class PluginManifest
{
    public required string DisplayName { get; set; }
    public List<ChannelManifest> InputChannels { get; set; } = new();
    public List<ChannelManifest> OutputChannels { get; set; } = new();
    public string? GlobalState { get; set; }
}

public sealed class ChannelManifest
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}
