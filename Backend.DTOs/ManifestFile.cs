namespace Backend.Models;

public record ManifestFile
{
    public required string DisplayName { get; set; }
    public ManifestFileChannelInfo? GlobalState { get; set; }
    public ManifestFileChannelInfo? InputChannel { get; set; }
    public List<ManifestFileChannelInfo> OutputChannels { get; set; } = [];
    public ManifestFileConfigurationInfo Configuration { get; set; } = new();
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

public record ManifestFileConfigurationInfo
{
    public List<ManifestFileConfigurationRequirementInfo> Requirements { get; set; } = [];
}

public record ManifestFileConfigurationRequirementInfo
{
    public string DomainId { get; set; } = "";
    public string OptionsType { get; set; } = "";
    public string OptionsName { get; set; } = "";
    public string Key { get; set; } = "";
    public string PropertyPath { get; set; } = "";
    public string ClrType { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Required { get; set; }
    public bool Secret { get; set; }
    public string? Description { get; set; }
    public List<string> EnumValues { get; set; } = [];
    public bool IsCollection { get; set; }
}
