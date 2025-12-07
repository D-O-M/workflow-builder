namespace Backend.Engine.Messages;

public record WorkflowTaskInitialisationMessage
{
    public Guid WorkflowInstanceId { get; init; }
    public int WorkflowBlueprintId { get; init; }

    public Guid WorkflowTaskInstanceId { get; init; }
    public int WorkflowTaskId { get; init; }

    public Guid PreviousWorkflowTaskInstanceId { get; init; }
}
