namespace Backend.Models.Observability;

public sealed class WorkflowLifecycleLogEntry
{
    public required string EventCategory { get; init; }
    public required string EventName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Guid WorkflowInstanceId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string BlueprintVersionName { get; init; }
    public required string Status { get; init; }
    public string? ExecutionOutcome { get; init; }
    public string? FailureMessage { get; init; }
}
