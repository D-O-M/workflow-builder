namespace Backend.Models.ViewModels;

public sealed record PhasedProcessOverviewViewModel(
    Guid PhasedProcessId,
    int PhasedBlueprintDefinitionId,
    string PhasedBlueprintName,
    int PhasedBlueprintVersionId,
    int PhasedBlueprintVersionNumber,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    Guid? CurrentPhaseId,
    IReadOnlyList<PhasedProcessPhaseOverviewViewModel> Phases,
    IReadOnlyList<PhasedProcessTransitionViewModel> Transitions);

public sealed record PhasedProcessPhaseOverviewViewModel(
    Guid PhaseId,
    int PhaseIndex,
    string PhaseName,
    int BlueprintDefinitionId,
    string BlueprintName,
    int? BlueprintVersionId,
    int? BlueprintVersionNumber,
    string VersionPolicy,
    Guid? ChildProcessId,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureMessage);

public sealed record PhasedProcessTransitionViewModel(Guid TransitionId, Guid SourcePhaseId, Guid DestinationPhaseId);

public sealed record PhasedProcessHandoffViewModel(
    Guid TransitionId,
    Guid SourcePhaseId,
    Guid DestinationPhaseId,
    IReadOnlyList<PhasedProcessHandoffMappingViewModel> Mappings,
    IReadOnlyList<PhasedProcessUnattributedValueViewModel> UnattributedValues);

public sealed record PhasedProcessHandoffMappingViewModel(
    Guid? SourceNodeId,
    string? SourceNodeName,
    string? SourceChannel,
    string SourceProperty,
    Guid TargetNodeId,
    string? TargetNodeName,
    string TargetProperty,
    bool HasValue,
    string? ValueJson);

public sealed record PhasedProcessUnattributedValueViewModel(string Property, string ValueJson);
