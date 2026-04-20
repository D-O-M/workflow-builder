namespace Backend.Models.ViewModels;

public sealed class RecentProcessesViewModel
{
    public required string Window { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public List<RecentProcessViewModel> Processes { get; init; } = [];
}

public sealed class RecentProcessViewModel
{
    public required Guid ProcessId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required string BlueprintName { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? FailureMessage { get; init; }
}
