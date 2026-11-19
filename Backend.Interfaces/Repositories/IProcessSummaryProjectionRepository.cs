using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessSummaryProjectionRepository
{
    Task<ProcessSummaryProjectionRecord?> GetByIdAsync(
        string id,
        string partitionKey,
        CancellationToken cancellationToken);

    Task<ProcessSummaryProjectionRecord> UpsertAsync(
        ProcessSummaryProjectionRecord record,
        CancellationToken cancellationToken);

    async Task<bool> TrySaveProjectionAsync(ProcessSummaryProjectionRecord record, string? expectedETag, CancellationToken cancellationToken)
    {
        await UpsertAsync(record, cancellationToken);
        return true;
    }

    Task<ProcessSummaryProjectionPage> QueryRecentAsync(
        ProcessSummaryProjectionScope scope,
        string tenantId,
        int? blueprintDefinitionId,
        IReadOnlyCollection<int>? blueprintVersionIds,
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken);
}
