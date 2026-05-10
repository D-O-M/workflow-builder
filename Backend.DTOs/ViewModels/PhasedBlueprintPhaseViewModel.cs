namespace Backend.Models.ViewModels;

public sealed class PhasedBlueprintPhaseViewModel
{
    public Guid PhaseId { get; init; }

    public int PhaseIndex { get; init; }

    public string Name { get; init; } = string.Empty;

    public int BlueprintDefinitionId { get; init; }

    public string BlueprintName { get; init; } = string.Empty;

    public int? BlueprintVersionId { get; init; }

    public int? BlueprintVersionNumber { get; init; }

    public string VersionPolicy { get; init; } = "Pinned";
}

