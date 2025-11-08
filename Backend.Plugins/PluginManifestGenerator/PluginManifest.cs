using System.Collections.Generic;

namespace PluginManifestGenerator;

public class PluginManifest
{
    public string PluginClassName { get; set; } = string.Empty;
    public List<ChannelManifest> InputChannels { get; set; } = new();
    public List<ChannelManifest> OutputChannels { get; set; } = new();
}
