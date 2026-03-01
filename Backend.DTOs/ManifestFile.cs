namespace Backend.Models;

public record ManifestFile
{
    public required string DisplayName { get; set; }
    public required string GlobalState { get; set; }
    public required ManifestFileChannelInfo InputChannel { get; set; }
    public required List<ManifestFileChannelInfo> OutputChannels { get; set; }
}

public record ManifestFileChannelInfo
{
    public required string Name { get; set; }
    public required string Type { get; set; }
}
