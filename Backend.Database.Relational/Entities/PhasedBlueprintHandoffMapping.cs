namespace Backend.Database.Relational.Entities;

public enum PhasedBlueprintHandoffSourceKind
{
    Output = 0,
    GlobalState = 1
}

public sealed class PhasedBlueprintHandoffMapping
{
    public int Id { get; set; }

    public Guid PhasedBlueprintTransitionId { get; set; }
    public PhasedBlueprintTransition PhasedBlueprintTransition { get; set; } = null!;

    public PhasedBlueprintHandoffSourceKind SourceKind { get; set; } = PhasedBlueprintHandoffSourceKind.Output;

    public Guid? SourceBlueprintNodeId { get; set; }

    public string? SourceChannelName { get; set; }

    public string SourceProperty { get; set; } = null!;

    public double BridgePosition { get; set; } = 0.5d;

    public Guid TargetBlueprintNodeId { get; set; }

    public string TargetProperty { get; set; } = null!;
}

