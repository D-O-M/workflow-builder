namespace Backend.Database.Relational.Entities;

public enum PhasedBlueprintPhaseVersionPolicy
{
    Pinned = 0,
    Latest = 1
}

public sealed class PhasedBlueprintPhase
{
    public Guid Id { get; set; }

    public int PhasedBlueprintVersionId { get; set; }
    public PhasedBlueprintVersion PhasedBlueprintVersion { get; set; } = null!;

    public int PhaseIndex { get; set; }

    public string Name { get; set; } = null!;

    public int BlueprintDefinitionId { get; set; }
    public BlueprintDefinition BlueprintDefinition { get; set; } = null!;

    public int? BlueprintVersionId { get; set; }
    public BlueprintVersion? BlueprintVersion { get; set; }

    public PhasedBlueprintPhaseVersionPolicy VersionPolicy { get; set; } = PhasedBlueprintPhaseVersionPolicy.Pinned;
}

