namespace Backend.Interfaces.Repositories;

public sealed record PhasedProcessSearchCriteria(
    IReadOnlyList<string>? DefinitionIds,
    Guid? ProcessId,
    string? Status,
    DateTimeOffset CreatedTo);

public sealed record PhasedProcessSearchPhase(string PhaseId, int PhaseIndex, string PhaseName, string Status);

public sealed record PhasedProcessSearchRow(
    Guid Id, string PhasedBlueprintDefinitionId, int PhasedBlueprintVersionId,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, string Status, string? CurrentPhaseId,
    IReadOnlyList<PhasedProcessSearchPhase> Phases);

public sealed record PhasedProcessSearchPage(IReadOnlyList<PhasedProcessSearchRow> Items, string? ContinuationToken);

public interface IPhasedProcessSearchReader
{
    Task<PhasedProcessSearchPage> ReadPageAsync(
        PhasedProcessSearchCriteria criteria, string? continuationToken, int pageSize, CancellationToken cancellationToken);
}
