namespace Backend.Database.State.Objects;

public sealed class WorkflowStateSummaryPage
{
    public required IReadOnlyList<WorkflowStateSummary> Items { get; init; }
    public string? ContinuationToken { get; init; }
}
