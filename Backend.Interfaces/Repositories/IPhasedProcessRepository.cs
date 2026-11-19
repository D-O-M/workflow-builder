using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public sealed record PhasedProcessOverviewState(
    int PhasedBlueprintVersionId, string Status, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    string? CurrentPhaseId, IReadOnlyList<PhasedProcessOverviewPhase> Phases);

public sealed record PhasedProcessOverviewPhase(
    string PhaseId, int PhaseIndex, string PhaseName, int BlueprintDefinitionId, int? BlueprintVersionId,
    string? ChildProcessId, string Status, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string? FailureMessage);

public interface IPhasedProcessRepository
{
    Task<PhasedProcessRecord> CreateAsync(PhasedProcessRecord record, CancellationToken cancellationToken);

    Task<PhasedProcessRecord> GetAsync(Guid phasedProcessId, int phasedBlueprintDefinitionId, CancellationToken cancellationToken);

    Task<PhasedProcessRecord> GetAsync(Guid phasedProcessId, string phasedBlueprintDefinitionId, CancellationToken cancellationToken);

    Task<PhasedProcessOverviewState> GetOverviewAsync(Guid phasedProcessId, int phasedBlueprintDefinitionId, CancellationToken cancellationToken);

    Task<PhasedProcessRecord> UpdateAsync(PhasedProcessRecord record, CancellationToken cancellationToken);

    Task<bool> TryUpdateAsync(PhasedProcessRecord record, CancellationToken cancellationToken);
}

