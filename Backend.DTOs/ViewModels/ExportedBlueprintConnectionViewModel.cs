namespace Backend.Models.ViewModels;

public sealed class ExportedBlueprintConnectionViewModel
{
    public required string ChannelName { get; init; }
    public required Guid ConnectedTaskKey { get; init; }
    public int Anchor { get; init; }
    public required string Side { get; init; }
    public int Position { get; init; }
}
