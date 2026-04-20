namespace Backend.Models.ViewModels;

public sealed class NodeObservabilitySummaryViewModel
{
    public required string Window { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string BlueprintVersionName { get; init; }
    public List<NodeObservabilitySummaryItemViewModel> Nodes { get; init; } = [];
}

public sealed class NodeObservabilitySummaryItemViewModel
{
    public required Guid BlueprintTaskId { get; init; }
    public required string TaskName { get; init; }
    public required int TaskDefinitionId { get; init; }
    public int SuccessfulCount { get; init; }
    public int FailedCount { get; init; }
    public int TotalCount => SuccessfulCount + FailedCount;
    public double SuccessRate => TotalCount == 0 ? 0 : (double)SuccessfulCount / TotalCount;
}
