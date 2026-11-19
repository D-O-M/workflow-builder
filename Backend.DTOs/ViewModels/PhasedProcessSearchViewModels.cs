namespace Backend.Models.ViewModels;

public sealed record PhasedProcessSearchPageViewModel(
    IReadOnlyList<PhasedProcessSummaryViewModel> Items,
    string? ContinuationToken);

public sealed record PhasedProcessSummaryViewModel(
    Guid PhasedProcessId,
    int PhasedBlueprintDefinitionId,
    string PhasedBlueprintName,
    int PhasedBlueprintVersionId,
    int PhasedBlueprintVersionNumber,
    string Status,
    string? CurrentPhaseName,
    int? CurrentPhaseIndex,
    int PhaseCount,
    int CompletedPhaseCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
