using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class ProcessStepFinalizationRepository
    : CosmosRepository<ProcessStepFinalizationRecord>,
      IProcessStepFinalizationRepository
{
    private const string RecoverablePredicate = """
        (
            (
                IS_DEFINED(c.updatedAt)
                AND NOT IS_NULL(c.updatedAt)
                AND c.updatedAt <= @cutoff
            )
            OR
            (
                (NOT IS_DEFINED(c.updatedAt) OR IS_NULL(c.updatedAt))
                AND c.createdAt <= @cutoff
            )
        )
        AND (
            NOT IS_DEFINED(c.lastErrorKind)
            OR IS_NULL(c.lastErrorKind)
            OR c.lastErrorKind != @finalizationFailed
        )
        AND (
            NOT IS_DEFINED(c.retryCount)
            OR IS_NULL(c.retryCount)
            OR c.retryCount < @maxRetryCount
        )
        AND (
            (
                c.stage != @queueMessageCompletionPrepared
                AND c.stage != @legacyQueueMessageCompleted
                AND c.stage != @recoveredWithoutQueueMessage
            )
            OR
            (
                (
                    c.stage = @queueMessageCompletionPrepared
                    OR c.stage = @legacyQueueMessageCompleted
                )
                AND IS_DEFINED(c.lastErrorKind)
                AND NOT IS_NULL(c.lastErrorKind)
            )
        )
        """;

    public ProcessStepFinalizationRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.Id))
    {
    }

    public Task<ProcessStepFinalizationRecord?> GetAsync(
        Guid processStepId,
        CancellationToken cancellationToken) =>
        GetAsync(
            processStepId.ToString(),
            new PartitionKey(processStepId.ToString()),
            cancellationToken);

    public async Task<(bool Created, ProcessStepFinalizationRecord Record)> CreateIfNotExistsAsync(
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, (await CreateAsync(record, cancellationToken)).Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetAsync(
                Guid.Parse(record.Id),
                cancellationToken);
            return (false, existing ?? throw new InvalidOperationException(
                $"Finalization record '{record.Id}' already exists but could not be read."));
        }
    }

    public async Task<bool> TryUpdateAsync(
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReplaceAsync(record, cancellationToken, record.ETag);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    public Task<IReadOnlyList<ProcessStepFinalizationRecord>> GetRecoverableFinalizationsAsync(
        DateTimeOffset now,
        TimeSpan minRecordAge,
        int maxRetryCount,
        int maxItemCount,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxItemCount, 1, 500);
        var query = ApplyRecoverableParameters(new QueryDefinition($"""
            SELECT TOP {limit} *
            FROM c
            WHERE {RecoverablePredicate}
            ORDER BY c.updatedAt ASC
            """),
            now,
            minRecordAge,
            maxRetryCount);

        return QueryRecoverableRecordsAsync(query, cancellationToken);
    }

    public async Task<ProcessStepFinalizationRecoveryBacklogSnapshot> GetRecoveryBacklogSnapshotAsync(
        DateTimeOffset now,
        TimeSpan minRecordAge,
        int maxRetryCount,
        CancellationToken cancellationToken)
    {
        var countQuery = ApplyRecoverableParameters(new QueryDefinition($"""
            SELECT VALUE COUNT(1)
            FROM c
            WHERE {RecoverablePredicate}
            """),
            now,
            minRecordAge,
            maxRetryCount);

        using var countFeed = Container.GetItemQueryIterator<long>(countQuery);
        var count = 0L;
        while (countFeed.HasMoreResults)
        {
            var page = await countFeed.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            count = page.FirstOrDefault();
            break;
        }

        if (count == 0)
        {
            return new ProcessStepFinalizationRecoveryBacklogSnapshot(0, null);
        }

        var oldestRecord = (await GetRecoverableFinalizationsAsync(
                now,
                minRecordAge,
                maxRetryCount,
                maxItemCount: 1,
                cancellationToken)
            .ConfigureAwait(false))
            .FirstOrDefault();
        var oldest = oldestRecord?.UpdatedAt ?? oldestRecord?.CreatedAt;

        return new ProcessStepFinalizationRecoveryBacklogSnapshot(count, oldest);
    }

    private async Task<IReadOnlyList<ProcessStepFinalizationRecord>> QueryRecoverableRecordsAsync(
        QueryDefinition query,
        CancellationToken cancellationToken)
    {
        var records = await QueryToListAsync(query, cancellationToken).ConfigureAwait(false);
        return records;
    }

    private static QueryDefinition ApplyRecoverableParameters(
        QueryDefinition query,
        DateTimeOffset now,
        TimeSpan minRecordAge,
        int maxRetryCount)
    {
        var cutoff = now - minRecordAge;
        return query
            .WithParameter("@cutoff", cutoff)
            .WithParameter("@maxRetryCount", Math.Max(0, maxRetryCount))
            .WithParameter("@finalizationFailed", ProcessStepFinalizationErrorKind.FinalizationFailed.ToString())
            .WithParameter("@queueMessageCompletionPrepared", ProcessStepFinalizationStage.QueueMessageCompletionPrepared.ToString())
            .WithParameter("@legacyQueueMessageCompleted", "QueueMessageCompleted")
            .WithParameter("@recoveredWithoutQueueMessage", ProcessStepFinalizationStage.RecoveredWithoutQueueMessage.ToString());
    }
}
