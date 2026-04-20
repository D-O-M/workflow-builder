namespace Backend.Models.Observability;

public sealed class ProcessLifecycleLogEntry
{
    public required string EventCategory { get; init; }
    public required string EventName { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Guid ProcessId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required string BlueprintName { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string Status { get; init; }
    public string? ProcessOutcome { get; init; }
    public string? FailureMessage { get; init; }
}
