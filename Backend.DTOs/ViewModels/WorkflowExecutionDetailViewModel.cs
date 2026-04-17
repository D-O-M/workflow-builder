namespace Backend.Models.ViewModels;

public sealed class WorkflowExecutionDetailViewModel
{
    public required Guid WorkflowInstanceId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required string BlueprintName { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string BlueprintVersionName { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public required Guid StartingTaskInstanceId { get; init; }
    public List<WorkflowExecutionNodeViewModel> Nodes { get; init; } = [];
}
