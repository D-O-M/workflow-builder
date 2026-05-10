namespace Backend.Database.Relational.Entities;

public sealed class PhasedBlueprintVersion
{
    public int Id { get; set; }

    public int PhasedBlueprintDefinitionId { get; set; }
    public PhasedBlueprintDefinition PhasedBlueprintDefinition { get; set; } = null!;

    public int Version { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<PhasedBlueprintPhase> Phases { get; set; } = new();

    public List<PhasedBlueprintTransition> Transitions { get; set; } = new();
}

