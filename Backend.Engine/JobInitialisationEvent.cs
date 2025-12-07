namespace Backend.Engine;

public record JobInitialisationEvent(
    Guid WorkflowInstanceId,
    Guid WorkflowTaskInstanceId,
    Guid PreviousWorkflowTaskInstanceId,
    DateTimeOffset Deadline);
