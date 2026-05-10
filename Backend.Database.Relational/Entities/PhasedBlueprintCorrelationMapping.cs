namespace Backend.Database.Relational.Entities;

public sealed class PhasedBlueprintCorrelationMapping
{
    public int Id { get; set; }

    public Guid PhasedBlueprintTransitionId { get; set; }
    public PhasedBlueprintTransition PhasedBlueprintTransition { get; set; } = null!;

    public string SourceResultProperty { get; set; } = null!;

    public string TriggerProperty { get; set; } = null!;
}

