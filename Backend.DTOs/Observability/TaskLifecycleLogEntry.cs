namespace Backend.Models.Observability;

public sealed class TaskLifecycleLogEntry
{
    public required string EventCategory { get; init; }
    public required string EventName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Guid WorkflowInstanceId { get; init; }
    public required Guid WorkflowTaskInstanceId { get; init; }
    public required Guid BlueprintTaskId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string BlueprintVersionName { get; init; }
    public required string TaskName { get; init; }
    public required string PluginName { get; init; }
    public required int TaskDefinitionId { get; init; }
    public required int TaskVersionId { get; init; }
    public required string TaskVersion { get; init; }
    public required string Status { get; init; }
    public string? ExecutionOutcome { get; init; }
    public double? DurationMs { get; init; }
    public string? FailureMessage { get; init; }
}
