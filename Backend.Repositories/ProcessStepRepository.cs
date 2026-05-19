using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;
using System.Net;
using System.Text.Json;

namespace Backend.Repositories;

public class ProcessStepRepository
    : CosmosRepository<ProcessStepRecord>,
    IProcessStepRepository
{
    public ProcessStepRepository(Container container)
        : base(container, wo => wo.Id, wo => new PartitionKey(wo.ProcessId))
    { }

    public async Task<ProcessStepRecord> GetProcessStepById(
        Guid taskInstanceId,
        Guid processId,
        CancellationToken cancellationToken)
    {
        var taskInstance = await GetAsync(
            taskInstanceId.ToString(),
            new PartitionKey(processId.ToString()),
            cancellationToken);

        if (taskInstance is null)
        {
            throw new EntityNotFoundException<ProcessStepRecord>(
                taskInstanceId.ToString());
        }

        return taskInstance;
    }

    public async Task<ProcessStepRecord> CreateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken)
    {
        return (await CreateAsync(state, cancellationToken)).Resource;
    }

    public async Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
        ProcessStepRecord state,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, await CreateProcessStep(state, cancellationToken));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetProcessStepById(
                Guid.Parse(state.Id),
                Guid.Parse(state.ProcessId),
                cancellationToken);
            return (false, existing);
        }
    }

    public async Task<IReadOnlyList<(bool Created, ProcessStepRecord State)>> CreateProcessStepsIfNotExists(
        IReadOnlyCollection<ProcessStepRecord> states,
        CancellationToken cancellationToken)
    {
        if (states.Count == 0)
        {
            return [];
        }

        var results = new List<(bool Created, ProcessStepRecord State)>(states.Count);
        foreach (var partitionGroup in states.GroupBy(state => state.ProcessId, StringComparer.Ordinal))
        {
            foreach (var chunk in partitionGroup.Chunk(100))
            {
                var batch = Container.CreateTransactionalBatch(new PartitionKey(partitionGroup.Key));
                foreach (var state in chunk)
                {
                    batch.CreateItem(state);
                }

                using var response = await batch.ExecuteAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    for (var index = 0; index < chunk.Length; index++)
                    {
                        var resource = response.GetOperationResultAtIndex<ProcessStepRecord>(index).Resource;
                        results.Add((true, resource));
                    }

                    continue;
                }

                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                {
                    foreach (var state in chunk)
                    {
                        results.Add(await CreateProcessStepIfNotExists(state, cancellationToken));
                    }

                    continue;
                }

                throw new InvalidOperationException(
                    $"Could not create process steps in a Cosmos transactional batch. Status code: {response.StatusCode}.");
            }
        }

        return results;
    }

    public async Task<(bool Claimed, ProcessStepRecord? State)> TryClaimProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        int taskDefinitionId,
        int allowedMajor,
        int allowedMinor,
        int resolvedTaskVersionId,
        string resolvedTaskVersion,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var operations = new List<PatchOperation>
        {
            PatchOperation.Set("/status", ProcessStepStatus.InProgress.ToString()),
            PatchOperation.Set("/taskDefinitionId", taskDefinitionId),
            PatchOperation.Set("/allowedMajor", allowedMajor),
            PatchOperation.Set("/allowedMinor", allowedMinor),
            PatchOperation.Set("/resolvedTaskVersionId", resolvedTaskVersionId),
            PatchOperation.Set("/resolvedTaskVersion", resolvedTaskVersion),
            PatchOperation.Set("/updatedAt", startedAt),
            PatchOperation.Set("/startedAt", startedAt)
        };

        try
        {
            var response = await Container.PatchItemAsync<ProcessStepRecord>(
                taskInstanceId.ToString(),
                new PartitionKey(processId.ToString()),
                operations,
                new PatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE c.status = 'Pending'"
                },
                cancellationToken);

            return (true, response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return (false, null);
        }
    }

    public async Task<bool> TryResetProcessStepToPendingAsync(
        Guid taskInstanceId,
        Guid processId,
        DateTimeOffset resetAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await Container.PatchItemAsync<ProcessStepRecord>(
                taskInstanceId.ToString(),
                new PartitionKey(processId.ToString()),
                [
                    PatchOperation.Set("/status", ProcessStepStatus.Pending.ToString()),
                    PatchOperation.Set("/updatedAt", resetAt),
                    PatchOperation.Set<object?>("/startedAt", null)
                ],
                new PatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE c.status = 'InProgress'"
                },
                cancellationToken);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
        Guid processId,
        CancellationToken cancellationToken)
    {
        var processIdString = processId.ToString();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.processId = @processId ORDER BY c.createdAt ASC")
            .WithParameter("@processId", processIdString);

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(processIdString)
            });
    }

    public async Task<IReadOnlyList<ProcessStepRecord>> GetDueStepDispatchesAsync(
        DateTimeOffset now,
        int maxItemCount,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            """
            SELECT TOP @maxItemCount *
            FROM c
            WHERE c.status = 'Pending'
              AND (
                NOT IS_DEFINED(c.dispatch)
                OR IS_NULL(c.dispatch)
                OR c.dispatch.status = 'Pending'
                OR c.dispatch.status = 'RetryableDispatchError'
                OR (
                    c.dispatch.status = 'Dispatching'
                    AND IS_DEFINED(c.dispatch.lockedUntil)
                    AND c.dispatch.lockedUntil <= @now
                )
              )
              AND (
                NOT IS_DEFINED(c.dispatch)
                OR IS_NULL(c.dispatch)
                OR NOT IS_DEFINED(c.dispatch.nextAttemptAt)
                OR IS_NULL(c.dispatch.nextAttemptAt)
                OR c.dispatch.nextAttemptAt <= @now
              )
            ORDER BY c.createdAt ASC
            """)
            .WithParameter("@maxItemCount", Math.Max(1, maxItemCount))
            .WithParameter("@now", now);

        return await QueryToListAsync(query, cancellationToken);
    }

    public async Task<ProcessStepDispatchBacklogSnapshot> GetDispatchBacklogSnapshotAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var countQuery = new QueryDefinition(
            """
            SELECT VALUE COUNT(1)
            FROM c
            WHERE c.status = 'Pending'
              AND (
                NOT IS_DEFINED(c.dispatch)
                OR IS_NULL(c.dispatch)
                OR c.dispatch.status = 'Pending'
                OR c.dispatch.status = 'RetryableDispatchError'
                OR c.dispatch.status = 'Dispatching'
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
            WHERE c.status = 'Pending'
              AND (
                NOT IS_DEFINED(c.dispatch)
                OR IS_NULL(c.dispatch)
                OR c.dispatch.status = 'Pending'
                OR c.dispatch.status = 'RetryableDispatchError'
                OR c.dispatch.status = 'Dispatching'
              )
            ORDER BY c.createdAt ASC
            """);
        var oldest = (await QueryToListAsync(oldestQuery, cancellationToken)).FirstOrDefault();

        return new ProcessStepDispatchBacklogSnapshot(
            pendingCount,
            oldest?.Dispatch?.CreatedAt ?? oldest?.CreatedAt);
    }

    public async Task<(bool Claimed, ProcessStepRecord? State)> TryClaimStepDispatchAsync(
        ProcessStepRecord state,
        string leaseId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (state.Status is not ProcessStepStatus.Pending)
        {
            return (false, null);
        }

        if (!IsDispatchDue(state.Dispatch, now))
        {
            return (false, null);
        }

        var dispatch = CreateClaimedDispatchState(state, leaseId, now, leaseDuration);
        var options = new PatchItemRequestOptions
        {
            FilterPredicate = "FROM c WHERE c.status = 'Pending'"
        };
        if (!string.IsNullOrWhiteSpace(state.ETag))
        {
            options.IfMatchEtag = state.ETag;
        }

        try
        {
            var response = await Container.PatchItemAsync<ProcessStepRecord>(
                state.Id,
                new PartitionKey(state.ProcessId),
                [
                    PatchOperation.Set("/dispatch", dispatch)
                ],
                options,
                cancellationToken);

            return (true, response.Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return (false, null);
        }
    }

    public async Task<bool> MarkStepDispatchSucceededAsync(
        ProcessStepRecord state,
        string leaseId,
        DateTimeOffset dispatchedAt,
        CancellationToken cancellationToken)
    {
        var escapedLeaseId = EscapeSqlString(leaseId);
        try
        {
            await Container.PatchItemAsync<ProcessStepRecord>(
                state.Id,
                new PartitionKey(state.ProcessId),
                [
                    PatchOperation.Set("/dispatch/status", ProcessStepDispatchStatus.Dispatched.ToString()),
                    PatchOperation.Set("/dispatch/messageId", state.Id),
                    PatchOperation.Set("/dispatch/updatedAt", dispatchedAt),
                    PatchOperation.Set("/dispatch/dispatchedAt", dispatchedAt),
                    PatchOperation.Set<object?>("/dispatch/nextAttemptAt", null),
                    PatchOperation.Set<object?>("/dispatch/dispatchingStartedAt", null),
                    PatchOperation.Set<object?>("/dispatch/lockedUntil", null),
                    PatchOperation.Set<object?>("/dispatch/leaseId", null),
                    PatchOperation.Set<object?>("/dispatch/lastErrorKind", null),
                    PatchOperation.Set<object?>("/dispatch/lastErrorMessage", null),
                    PatchOperation.Set<object?>("/dispatch/lastErrorAt", null)
                ],
                new PatchItemRequestOptions
                {
                    FilterPredicate = $"FROM c WHERE IS_DEFINED(c.dispatch) AND c.dispatch.leaseId = '{escapedLeaseId}'"
                },
                cancellationToken);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> MarkStepDispatchRetryableErrorAsync(
        ProcessStepRecord state,
        string leaseId,
        ProcessStepDispatchErrorKind errorKind,
        string errorMessage,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        var escapedLeaseId = EscapeSqlString(leaseId);
        try
        {
            await Container.PatchItemAsync<ProcessStepRecord>(
                state.Id,
                new PartitionKey(state.ProcessId),
                [
                    PatchOperation.Set("/dispatch/status", ProcessStepDispatchStatus.RetryableDispatchError.ToString()),
                    PatchOperation.Set("/dispatch/updatedAt", failedAt),
                    PatchOperation.Set("/dispatch/nextAttemptAt", nextAttemptAt),
                    PatchOperation.Set<object?>("/dispatch/dispatchingStartedAt", null),
                    PatchOperation.Set<object?>("/dispatch/lockedUntil", null),
                    PatchOperation.Set<object?>("/dispatch/leaseId", null),
                    PatchOperation.Set("/dispatch/lastErrorKind", errorKind.ToString()),
                    PatchOperation.Set("/dispatch/lastErrorMessage", errorMessage),
                    PatchOperation.Set("/dispatch/lastErrorAt", failedAt)
                ],
                new PatchItemRequestOptions
                {
                    FilterPredicate = $"FROM c WHERE c.status = 'Pending' AND IS_DEFINED(c.dispatch) AND c.dispatch.leaseId = '{escapedLeaseId}'"
                },
                cancellationToken);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> MarkStepDispatchSkippedTerminalAsync(
        ProcessStepRecord state,
        string leaseId,
        DateTimeOffset skippedAt,
        CancellationToken cancellationToken)
    {
        var escapedLeaseId = EscapeSqlString(leaseId);
        try
        {
            await Container.PatchItemAsync<ProcessStepRecord>(
                state.Id,
                new PartitionKey(state.ProcessId),
                [
                    PatchOperation.Set("/dispatch/status", ProcessStepDispatchStatus.DispatchSkippedTerminal.ToString()),
                    PatchOperation.Set("/dispatch/updatedAt", skippedAt),
                    PatchOperation.Set<object?>("/dispatch/nextAttemptAt", null),
                    PatchOperation.Set<object?>("/dispatch/dispatchingStartedAt", null),
                    PatchOperation.Set<object?>("/dispatch/lockedUntil", null),
                    PatchOperation.Set<object?>("/dispatch/leaseId", null)
                ],
                new PatchItemRequestOptions
                {
                    FilterPredicate = $"FROM c WHERE IS_DEFINED(c.dispatch) AND c.dispatch.leaseId = '{escapedLeaseId}'"
                },
                cancellationToken);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> TryUpdateStepDispatchAsync(
        Guid processStepId,
        Guid processId,
        ProcessStepDispatchState dispatch,
        CancellationToken cancellationToken)
    {
        try
        {
            await Container.PatchItemAsync<ProcessStepRecord>(
                processStepId.ToString(),
                new PartitionKey(processId.ToString()),
                [
                    PatchOperation.Set("/dispatch", dispatch),
                    PatchOperation.Set("/updatedAt", dispatch.UpdatedAt ?? DateTimeOffset.UtcNow)
                ],
                new PatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE c.status = 'Pending'"
                },
                cancellationToken);

            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task CompleteProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        Dictionary<string, JsonElement>? effectiveInputData,
        Dictionary<string, JsonElement> outputData,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var operations = new List<PatchOperation>
        {
            PatchOperation.Set("/status", ProcessStepStatus.Completed.ToString()),
            PatchOperation.Set("/updatedAt", completedAt),
            PatchOperation.Set("/completedAt", completedAt),
            PatchOperation.Set<object?>("/failedAt", null),
            PatchOperation.Set<object?>("/errorMessage", null),
            PatchOperation.Set("/outputData", outputData)
        };

        if (effectiveInputData is not null)
        {
            operations.Add(PatchOperation.Set("/inputData", effectiveInputData));
        }

        try
        {
            await Container.PatchItemAsync<ProcessStepRecord>(
                taskInstanceId.ToString(),
                new PartitionKey(processId.ToString()),
                operations,
                new PatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE c.status != 'Completed' AND c.status != 'Failed'"
                },
                cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            // The success payload is already durable, or the step reached another terminal state.
        }
    }

    public async Task FailProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        Dictionary<string, JsonElement>? effectiveInputData,
        string? errorMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        var operations = new List<PatchOperation>
        {
            PatchOperation.Set("/status", ProcessStepStatus.Failed.ToString()),
            PatchOperation.Set("/updatedAt", failedAt),
            PatchOperation.Set("/failedAt", failedAt),
            PatchOperation.Set("/errorMessage", errorMessage)
        };

        if (effectiveInputData is not null)
        {
            operations.Add(PatchOperation.Set("/inputData", effectiveInputData));
        }

        try
        {
            await Container.PatchItemAsync<ProcessStepRecord>(
                taskInstanceId.ToString(),
                new PartitionKey(processId.ToString()),
                operations,
                new PatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE c.status != 'Completed' AND c.status != 'Failed'"
                },
                cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            // The failure payload is already durable, or the step reached another terminal state.
        }
    }

    public async Task<ProcessStepRecord> UpdateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken)
    {
        return (await ReplaceAsync(state, cancellationToken, state.ETag)).Resource;
    }

    public async Task<bool> TryUpdateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdateProcessStep(state, cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed || ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    private static ProcessStepDispatchState CreateClaimedDispatchState(
        ProcessStepRecord state,
        string leaseId,
        DateTimeOffset now,
        TimeSpan leaseDuration)
    {
        var dispatch = state.Dispatch ?? new ProcessStepDispatchState
        {
            Status = ProcessStepDispatchStatus.Pending,
            CreatedAt = state.CreatedAt
        };

        dispatch.Status = ProcessStepDispatchStatus.Dispatching;
        dispatch.MessageId = state.Id;
        dispatch.AttemptCount++;
        dispatch.UpdatedAt = now;
        dispatch.NextAttemptAt = null;
        dispatch.DispatchingStartedAt = now;
        dispatch.LockedUntil = now.Add(leaseDuration);
        dispatch.LeaseId = leaseId;
        return dispatch;
    }

    private static bool IsDispatchDue(
        ProcessStepDispatchState? dispatch,
        DateTimeOffset now) =>
        dispatch is null ||
        dispatch.Status is ProcessStepDispatchStatus.Pending ||
        dispatch.Status is ProcessStepDispatchStatus.RetryableDispatchError &&
            (!dispatch.NextAttemptAt.HasValue || dispatch.NextAttemptAt.Value <= now) ||
        dispatch.Status is ProcessStepDispatchStatus.Dispatching &&
            dispatch.LockedUntil.HasValue &&
            dispatch.LockedUntil.Value <= now;

    private static string EscapeSqlString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
