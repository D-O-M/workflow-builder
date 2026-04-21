namespace Backend.Models.ViewModels;

public sealed class BlueprintConnectionViewModel
{
    /// <summary>Resolved output channel id (Channels.Id), if known.</summary>
    public int? ChannelId { get; init; }

    /// <summary>Resolved output channel name, if known.</summary>
    public string? ChannelName { get; init; }

    public Guid BlueprintNodeId { get; init; }

    public int Anchor { get; init; }

    public string Side { get; init; } = null!;

    public int Position { get; init; }

    public List<BlueprintPropertyMappingViewModel> PropertyMappings { get; init; } = new();
}
