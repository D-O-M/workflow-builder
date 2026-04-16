namespace Backend.Plugins.Communication;

public sealed record WorkflowTaskInitialisationMessage
{
    public Guid WorkflowInstanceId { get; init; }
    public int WorkflowBlueprintId { get; init; }
    public Guid WorkflowTaskInstanceId { get; init; }
    public Guid BlueprintTaskId { get; init; }
    public Guid PreviousWorkflowTaskInstanceId { get; init; }
    public string LinkedOutputChannelName { get; init; } = string.Empty;
}
