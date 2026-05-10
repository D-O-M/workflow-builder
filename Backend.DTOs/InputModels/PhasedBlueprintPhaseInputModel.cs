using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class PhasedBlueprintPhaseInputModel
{
    public Guid PhaseId { get; init; }

    [Range(0, 3)]
    public int PhaseIndex { get; init; }

    [Required, MinLength(1)]
    public string Name { get; init; } = null!;

    [Range(1, int.MaxValue)]
    public int BlueprintDefinitionId { get; init; }

    [Range(1, int.MaxValue)]
    public int? BlueprintVersionId { get; init; }

    public string VersionPolicy { get; init; } = "Pinned";
}

