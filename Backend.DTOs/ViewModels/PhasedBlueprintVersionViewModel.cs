namespace Backend.Models.ViewModels;

public sealed class PhasedBlueprintVersionViewModel
{
    public int PhasedBlueprintVersionId { get; init; }

    public int PhasedBlueprintDefinitionId { get; init; }

    public int Version { get; init; }

    public string? Description { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public List<PhasedBlueprintPhaseViewModel> Phases { get; init; } = [];

    public List<PhasedBlueprintTransitionViewModel> Transitions { get; init; } = [];
}

