namespace Backend.Database.Relational.Entities;

public sealed class PhasedBlueprintTransition
{
    public Guid Id { get; set; }

    public int PhasedBlueprintVersionId { get; set; }
    public PhasedBlueprintVersion PhasedBlueprintVersion { get; set; } = null!;

    public Guid SourcePhaseId { get; set; }
    public PhasedBlueprintPhase SourcePhase { get; set; } = null!;

    public Guid DestinationPhaseId { get; set; }
    public PhasedBlueprintPhase DestinationPhase { get; set; } = null!;

    public List<PhasedBlueprintHandoffMapping> HandoffMappings { get; set; } = new();

    public List<PhasedBlueprintCorrelationMapping> CorrelationMappings { get; set; } = new();
}

