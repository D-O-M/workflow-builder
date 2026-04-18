namespace Backend.Models.Realtime;

public sealed class ExecutionSubscriptionSnapshotViewModel
{
    public List<int> BlueprintDefinitionIds { get; init; } = [];
    public List<Guid> WorkflowInstanceIds { get; init; } = [];
}
