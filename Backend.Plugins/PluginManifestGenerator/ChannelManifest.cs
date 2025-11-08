using System.Collections.Generic;

namespace PluginManifestGenerator;

public class ChannelManifest
{
    public string TypeName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<FieldManifest> Fields { get; set; } = new();
}
