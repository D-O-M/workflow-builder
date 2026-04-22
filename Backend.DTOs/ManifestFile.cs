namespace Backend.Models;

public record ManifestFile
{
    public required string DisplayName { get; set; }
    public ManifestFileChannelInfo? GlobalState { get; set; }
    public ManifestFileChannelInfo? InputChannel { get; set; }
    public List<ManifestFileChannelInfo> OutputChannels { get; set; } = [];
}

public record ManifestFileChannelInfo
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public List<ManifestFilePropertyInfo> Properties { get; set; } = [];
}

public record ManifestFilePropertyInfo
{
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public bool Required { get; set; }
    public List<string> EnumValues { get; set; } = [];
}
