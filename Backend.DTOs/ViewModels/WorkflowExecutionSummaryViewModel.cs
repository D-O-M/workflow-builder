namespace Backend.Models.ViewModels;

public sealed class WorkflowExecutionSummaryViewModel
{
    public required Guid WorkflowInstanceId { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string BlueprintVersionName { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public int CompletedTaskCount { get; init; }
    public int FailedTaskCount { get; init; }
    public int InProgressTaskCount { get; init; }
    public int PendingTaskCount { get; init; }
}
