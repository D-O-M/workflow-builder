namespace Backend.Models.Realtime;

public sealed class ExecutionNodesUpdatedEvent
{
    public required Guid WorkflowInstanceId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required int BlueprintVersionId { get; init; }
    public List<ExecutionNodeDelta> Nodes { get; init; } = [];
}
