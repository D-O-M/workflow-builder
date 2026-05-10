using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class PhasedBlueprintVersionInputModel
{
    [Required]
    [Range(1, int.MaxValue)]
    public int PhasedBlueprintDefinitionId { get; init; }

    [Range(1, int.MaxValue)]
    public int? BaseVersion { get; init; }

    public string? Description { get; init; }

    [Required, MinLength(1), MaxLength(4)]
    public List<PhasedBlueprintPhaseInputModel> Phases { get; init; } = [];

    public List<PhasedBlueprintTransitionInputModel> Transitions { get; init; } = [];
}

