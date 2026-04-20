namespace Backend.Models.ViewModels;

public sealed class WorkflowRecentRunsViewModel
{
    public required string Window { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public List<RecentWorkflowExecutionViewModel> Runs { get; init; } = [];
}

public sealed class RecentWorkflowExecutionViewModel
{
    public required Guid WorkflowInstanceId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string BlueprintVersionName { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public string? FailureMessage { get; init; }
}
