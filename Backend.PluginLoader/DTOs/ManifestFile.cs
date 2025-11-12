namespace Backend.PluginLoader.DTOs;

internal record ManifestFile
{
    public required string DisplayName { get; set; }
    public required string GlobalState { get; set; }
    public required List<ManifestFileChannelInfo> InputChannels { get; set; }
    public required List<ManifestFileChannelInfo> OutputChannels { get; set; }
}

internal record ManifestFileChannelInfo
{
    public required string Name { get; set; }
    public required string Type { get; set; }
}
