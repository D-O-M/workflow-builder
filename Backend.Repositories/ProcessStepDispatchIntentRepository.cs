using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class ProcessStepDispatchIntentRepository
    : CosmosRepository<ProcessStepDispatchIntentRecord>,
      IProcessStepDispatchIntentRepository
{
    public ProcessStepDispatchIntentRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.PartitionKey))
    {
    }

    public Task<ProcessStepDispatchIntentRecord?> GetIntentAsync(
        string id,
        string partitionKey,
        CancellationToken cancellationToken) =>
        GetAsync(id, new PartitionKey(partitionKey), cancellationToken);

    public async Task<(bool Created, ProcessStepDispatchIntentRecord Record)> CreateIntentIfNotExistsAsync(
        ProcessStepDispatchIntentRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, (await CreateAsync(record, cancellationToken)).Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetIntentAsync(
                record.Id,
                record.PartitionKey,
                cancellationToken);
            return (false, existing ?? throw new InvalidOperationException(
                $"Dispatch intent '{record.Id}' already exists but could not be read."));
        }
    }

    public async Task<IReadOnlyList<ProcessStepDispatchIntentRecord>> GetDueIntentsAsync(
        long dueBucket,
        int shard,
        int maxItemCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxItemCount, 1, 500);
        var partitionKey = ProcessStepDispatchIntentKeys.CreateDuePartitionKey(dueBucket, shard);
        var query = new QueryDefinition($"""
            SELECT TOP {limit} *
            FROM c
            WHERE c.partitionKey = @partitionKey
              AND (
                (
                    (c.status = @pending OR c.status = @retryable)
                    AND c.nextAttemptAt <= @now
                )
                OR (
                    c.status = @dispatching
                    AND IS_DEFINED(c.lockedUntil)
                    AND NOT IS_NULL(c.lockedUntil)
                    AND c.lockedUntil <= @now
                )
              )
            ORDER BY c.createdAt ASC
            """)
            .WithParameter("@partitionKey", partitionKey)
            .WithParameter("@pending", ProcessStepDispatchStatus.Pending.ToString())
            .WithParameter("@retryable", ProcessStepDispatchStatus.RetryableDispatchError.ToString())
            .WithParameter("@dispatching", ProcessStepDispatchStatus.Dispatching.ToString())
            .WithParameter("@now", now);

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey)
            });
    }

    public async Task<(bool Claimed, ProcessStepDispatchIntentRecord? Record)> TryClaimIntentAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (!IsDue(record, now))
        {
            return (false, null);
        }

        record.Status = ProcessStepDispatchStatus.Dispatching;
        record.MessageId = ProcessStepDispatchSchedule.CreateMessageId(Guid.Parse(record.ProcessStepId), record.Generation);
        record.AttemptCount++;
        record.UpdatedAt = now;
        record.DispatchingStartedAt = now;
        record.LockedUntil = now.Add(leaseDuration);
        record.LeaseId = leaseId;

        try
        {
            var response = await ReplaceAsync(record, cancellationToken, record.ETag);
            return (true, response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return (false, null);
        }
    }

    public Task<bool> MarkIntentDispatchedAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset dispatchedAt,
        CancellationToken cancellationToken) =>
        UpdateLeasedIntentAsync(
            record,
            leaseId,
            current =>
            {
                current.Status = ProcessStepDispatchStatus.Dispatched;
                current.MessageId = ProcessStepDispatchSchedule.CreateMessageId(Guid.Parse(current.ProcessStepId), current.Generation);
                current.UpdatedAt = dispatchedAt;
                current.DispatchedAt = dispatchedAt;
                current.DispatchingStartedAt = null;
                current.LockedUntil = null;
                current.LeaseId = null;
                current.LastErrorKind = null;
                current.LastErrorMessage = null;
                current.LastErrorAt = null;
            },
            cancellationToken);

    public Task<bool> MarkIntentRetryableErrorAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        ProcessStepDispatchErrorKind errorKind,
        string errorMessage,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken) =>
        UpdateLeasedIntentAsync(
            record,
            leaseId,
            current =>
            {
                current.Status = ProcessStepDispatchStatus.RetryableDispatchError;
                current.UpdatedAt = failedAt;
                current.NextAttemptAt = nextAttemptAt;
                current.DispatchingStartedAt = null;
                current.LockedUntil = null;
                current.LeaseId = null;
                current.LastErrorKind = errorKind;
                current.LastErrorMessage = errorMessage;
                current.LastErrorAt = failedAt;
            },
            cancellationToken);

    public Task<bool> MarkIntentSkippedTerminalAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset skippedAt,
        CancellationToken cancellationToken) =>
        UpdateLeasedIntentAsync(
            record,
            leaseId,
            current =>
            {
                current.Status = ProcessStepDispatchStatus.DispatchSkippedTerminal;
                current.UpdatedAt = skippedAt;
                current.DispatchingStartedAt = null;
                current.LockedUntil = null;
                current.LeaseId = null;
            },
            cancellationToken);

    public async Task<ProcessStepDispatchCursorRecord> GetOrCreateCursorAsync(
        int shard,
        long initialDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var partitionKey = ProcessStepDispatchIntentKeys.CreateCursorPartitionKey(shard);
        var id = ProcessStepDispatchIntentKeys.CreateCursorId(shard);
        try
        {
            var response = await Container.CreateItemAsync(
                new ProcessStepDispatchCursorRecord
                {
                    id = id,
                    PartitionKey = partitionKey,
                    Shard = shard,
                    NextDueBucket = initialDueBucket,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                },
                new PartitionKey(partitionKey),
                cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return await ReadCursorAsync(id, partitionKey, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Dispatch cursor '{id}' already exists but could not be read.");
        }
    }

    public async Task<bool> TryAdvanceCursorAsync(
        ProcessStepDispatchCursorRecord cursor,
        long nextDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (nextDueBucket <= cursor.NextDueBucket)
        {
            return true;
        }

        cursor.NextDueBucket = nextDueBucket;
        cursor.UpdatedAt = timestamp;
        try
        {
            await ReplaceCursorAsync(cursor, cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> TryRewindCursorAsync(
        int shard,
        long nextDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var cursor = await GetOrCreateCursorAsync(shard, nextDueBucket, timestamp, cancellationToken);
        if (cursor.NextDueBucket <= nextDueBucket)
        {
            return true;
        }

        cursor.NextDueBucket = nextDueBucket;
        cursor.UpdatedAt = timestamp;
        try
        {
            await ReplaceCursorAsync(cursor, cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<ProcessStepDispatchBacklogSnapshot> GetBacklogSnapshotAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var countQuery = new QueryDefinition(
            """
            SELECT VALUE COUNT(1)
            FROM c
            WHERE IS_DEFINED(c.processStepId)
              AND (
                c.status = 'Pending'
                OR c.status = 'RetryableDispatchError'
                OR c.status = 'Dispatching'
              )
            """);
        using var countIterator = Container.GetItemQueryIterator<long>(countQuery);
        var pendingCount = 0L;
        if (countIterator.HasMoreResults)
        {
            var page = await countIterator.ReadNextAsync(cancellationToken);
            pendingCount = page.FirstOrDefault();
        }

        if (pendingCount == 0)
        {
            return new ProcessStepDispatchBacklogSnapshot(0, null);
        }

        var oldestQuery = new QueryDefinition(
            """
            SELECT TOP 1 *
            FROM c
            WHERE IS_DEFINED(c.processStepId)
              AND (
                c.status = 'Pending'
                OR c.status = 'RetryableDispatchError'
                OR c.status = 'Dispatching'
              )
            ORDER BY c.createdAt ASC
            """);
        var oldest = (await QueryToListAsync(oldestQuery, cancellationToken)).FirstOrDefault();
        return new ProcessStepDispatchBacklogSnapshot(
            pendingCount,
            oldest?.CreatedAt);
    }

    private async Task<bool> UpdateLeasedIntentAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        Action<ProcessStepDispatchIntentRecord> mutate,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(record.LeaseId, leaseId, StringComparison.Ordinal))
        {
            return false;
        }

        mutate(record);
        try
        {
            await ReplaceAsync(record, cancellationToken, record.ETag);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<ProcessStepDispatchCursorRecord?> ReadCursorAsync(
        string id,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await Container.ReadItemAsync<ProcessStepDispatchCursorRecord>(
                id,
                new PartitionKey(partitionKey),
                cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private Task<ItemResponse<ProcessStepDispatchCursorRecord>> ReplaceCursorAsync(
        ProcessStepDispatchCursorRecord cursor,
        CancellationToken cancellationToken)
    {
        var options = new ItemRequestOptions();
        if (!string.IsNullOrWhiteSpace(cursor.ETag))
        {
            options.IfMatchEtag = cursor.ETag;
        }

        return Container.ReplaceItemAsync(
            cursor,
            cursor.Id,
            new PartitionKey(cursor.PartitionKey),
            options,
            cancellationToken);
    }

    private static bool IsDue(
        ProcessStepDispatchIntentRecord record,
        DateTimeOffset now) =>
        (record.Status is ProcessStepDispatchStatus.Pending or ProcessStepDispatchStatus.RetryableDispatchError &&
            record.NextAttemptAt <= now) ||
        record.Status is ProcessStepDispatchStatus.Dispatching &&
            record.LockedUntil.HasValue &&
            record.LockedUntil.Value <= now;
}
