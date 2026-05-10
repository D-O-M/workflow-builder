namespace Backend.Models.InputModels;

public sealed class PhasedBlueprintTransitionInputModel
{
    public Guid TransitionId { get; init; }

    public Guid SourcePhaseId { get; init; }

    public Guid DestinationPhaseId { get; init; }

    public List<PhasedBlueprintHandoffMappingInputModel> HandoffMappings { get; init; } = [];

    public List<PhasedBlueprintCorrelationMappingInputModel> CorrelationMappings { get; init; } = [];
}

