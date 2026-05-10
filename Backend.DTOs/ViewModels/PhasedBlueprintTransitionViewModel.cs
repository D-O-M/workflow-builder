namespace Backend.Models.ViewModels;

public sealed class PhasedBlueprintTransitionViewModel
{
    public Guid TransitionId { get; init; }

    public Guid SourcePhaseId { get; init; }

    public Guid DestinationPhaseId { get; init; }

    public List<PhasedBlueprintHandoffMappingViewModel> HandoffMappings { get; init; } = [];

    public List<PhasedBlueprintCorrelationMappingViewModel> CorrelationMappings { get; init; } = [];
}

public sealed class PhasedBlueprintHandoffMappingViewModel
{
    public string SourceKind { get; init; } = "Output";

    public Guid? SourceBlueprintNodeId { get; init; }

    public string? SourceChannelName { get; init; }

    public string SourceProperty { get; init; } = string.Empty;

    public double BridgePosition { get; init; } = 0.5d;

    public Guid TargetBlueprintNodeId { get; init; }

    public string TargetProperty { get; init; } = string.Empty;
}

public sealed class PhasedBlueprintCorrelationMappingViewModel
{
    public string SourceResultProperty { get; init; } = string.Empty;

    public string TriggerProperty { get; init; } = string.Empty;
}

