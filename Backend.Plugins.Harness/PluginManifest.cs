using Backend.PluginAbi;

namespace Backend.Plugins.Harness;

public sealed class PluginManifest
{
    public required string DisplayName { get; set; }
    public PluginAbiMetadata Abi { get; set; } = PluginAbiDefaults.CurrentMetadata;
    public ChannelManifest? InputChannel { get; set; }
    public List<ChannelManifest> OutputChannels { get; set; } = new();
}

public sealed class ChannelManifest
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}
