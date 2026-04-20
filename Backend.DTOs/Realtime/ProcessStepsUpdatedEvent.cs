namespace Backend.Models.Realtime;

public sealed class ProcessStepsUpdatedEvent
{
    public required Guid ProcessId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required int BlueprintVersionId { get; init; }
    public List<ProcessStepDelta> Nodes { get; init; } = [];
}
