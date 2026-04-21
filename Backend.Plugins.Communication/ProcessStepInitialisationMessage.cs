namespace Backend.Plugins.Communication;

public sealed record ProcessStepInitialisationMessage
{
    public Guid ProcessId { get; init; }
    public int ExecutableBlueprintId { get; init; }
    public Guid ProcessStepId { get; init; }
    public Guid BlueprintNodeId { get; init; }
}
