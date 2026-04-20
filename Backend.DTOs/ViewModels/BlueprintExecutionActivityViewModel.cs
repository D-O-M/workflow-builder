namespace Backend.Models.ViewModels;

public sealed class BlueprintExecutionActivityViewModel
{
    public required int BlueprintDefinitionId { get; init; }
    public required string BlueprintName { get; init; }
    public required int RecentExecutionCount { get; init; }
    public required DateTimeOffset LatestExecutionAt { get; init; }
}
