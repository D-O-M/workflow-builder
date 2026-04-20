namespace Backend.Models.ViewModels;

public sealed class BlueprintProcessActivityViewModel
{
    public required int BlueprintDefinitionId { get; init; }
    public required string BlueprintName { get; init; }
    public required int RecentProcessCount { get; init; }
    public required DateTimeOffset LatestProcessAt { get; init; }
}
