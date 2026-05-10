using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class PhasedBlueprintInputModel
{
    public string? Key { get; init; }

    [Required, MinLength(1)]
    public string Name { get; init; } = null!;

    public string? Description { get; init; }

    [Required, MinLength(1), MaxLength(4)]
    public List<PhasedBlueprintPhaseInputModel> Phases { get; init; } = [];

    public List<PhasedBlueprintTransitionInputModel> Transitions { get; init; } = [];
}

