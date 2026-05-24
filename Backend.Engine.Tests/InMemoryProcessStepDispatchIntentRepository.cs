using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;

namespace Backend.Engine.Tests;

internal sealed class InMemoryProcessStepDispatchIntentRepository : IProcessStepDispatchIntentRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Id, string PartitionKey), ProcessStepDispatchIntentRecord> _intents = [];
    private readonly Dictionary<int, ProcessStepDispatchCursorRecord> _cursors = [];

    public List<(long DueBucket, int Shard, int MaxItemCount)> DueQueries { get; } = [];

    public int BacklogSnapshotCalls { get; private set; }

    public IReadOnlyCollection<ProcessStepDispatchIntentRecord> Intents
    {
        get
        {
            lock (_gate)
            {
                return _intents.Values.ToList();
            }
        }
    }

    public Task<ProcessStepDispatchIntentRecord?> GetIntentAsync(
        string id,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _intents.TryGetValue((id, partitionKey), out var record);
            return Task.FromResult(record);
        }
    }

    public Task<(bool Created, ProcessStepDispatchIntentRecord Record)> CreateIntentIfNotExistsAsync(
        ProcessStepDispatchIntentRecord record,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (record.Id, record.PartitionKey);
            if (_intents.TryGetValue(key, out var existing))
            {
                return Task.FromResult((false, existing));
            }

            record.UpdatedAt = record.UpdatedAt == default ? record.CreatedAt : record.UpdatedAt;
            _intents[key] = record;
            return Task.FromResult((true, record));
        }
    }

    public Task<IReadOnlyList<ProcessStepDispatchIntentRecord>> GetDueIntentsAsync(
        long dueBucket,
        int shard,
        int maxItemCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            DueQueries.Add((dueBucket, shard, maxItemCount));
            var partitionKey = ProcessStepDispatchIntentKeys.CreateDuePartitionKey(dueBucket, shard);
            return Task.FromResult<IReadOnlyList<ProcessStepDispatchIntentRecord>>(_intents.Values
                .Where(record => string.Equals(record.PartitionKey, partitionKey, StringComparison.Ordinal) &&
                    IsDue(record, now))
                .OrderBy(record => record.CreatedAt)
                .Take(maxItemCount)
                .ToList());
        }
    }

    public Task<(bool Claimed, ProcessStepDispatchIntentRecord? Record)> TryClaimIntentAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (record.Id, record.PartitionKey);
            if (!_intents.TryGetValue(key, out var current) ||
                !IsDue(current, now))
            {
                return Task.FromResult<(bool, ProcessStepDispatchIntentRecord?)>((false, null));
            }

            current.Status = ProcessStepDispatchStatus.Dispatching;
            current.MessageId = current.ProcessStepId;
            current.AttemptCount++;
            current.UpdatedAt = now;
            current.DispatchingStartedAt = now;
            current.LockedUntil = now.Add(leaseDuration);
            current.LeaseId = leaseId;
            return Task.FromResult<(bool, ProcessStepDispatchIntentRecord?)>((true, current));
        }
    }

    public Task<bool> MarkIntentDispatchedAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset dispatchedAt,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetLeased(record, leaseId, out var current))
            {
                return Task.FromResult(false);
            }

            current.Status = ProcessStepDispatchStatus.Dispatched;
            current.MessageId = current.ProcessStepId;
            current.UpdatedAt = dispatchedAt;
            current.DispatchedAt = dispatchedAt;
            current.DispatchingStartedAt = null;
            current.LockedUntil = null;
            current.LeaseId = null;
            current.LastErrorKind = null;
            current.LastErrorMessage = null;
            current.LastErrorAt = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkIntentRetryableErrorAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        ProcessStepDispatchErrorKind errorKind,
        string errorMessage,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetLeased(record, leaseId, out var current))
            {
                return Task.FromResult(false);
            }

            current.Status = ProcessStepDispatchStatus.RetryableDispatchError;
            current.UpdatedAt = failedAt;
            current.NextAttemptAt = nextAttemptAt;
            current.DispatchingStartedAt = null;
            current.LockedUntil = null;
            current.LeaseId = null;
            current.LastErrorKind = errorKind;
            current.LastErrorMessage = errorMessage;
            current.LastErrorAt = failedAt;
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkIntentSkippedTerminalAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset skippedAt,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!TryGetLeased(record, leaseId, out var current))
            {
                return Task.FromResult(false);
            }

            current.Status = ProcessStepDispatchStatus.DispatchSkippedTerminal;
            current.UpdatedAt = skippedAt;
            current.DispatchingStartedAt = null;
            current.LockedUntil = null;
            current.LeaseId = null;
            return Task.FromResult(true);
        }
    }

    public Task<ProcessStepDispatchCursorRecord> GetOrCreateCursorAsync(
        int shard,
        long initialDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cursors.TryGetValue(shard, out var existing))
            {
                return Task.FromResult(existing);
            }

            var cursor = new ProcessStepDispatchCursorRecord
            {
                id = ProcessStepDispatchIntentKeys.CreateCursorId(shard),
                PartitionKey = ProcessStepDispatchIntentKeys.CreateCursorPartitionKey(shard),
                Shard = shard,
                NextDueBucket = initialDueBucket,
                CreatedAt = timestamp,
                UpdatedAt = timestamp
            };
            _cursors[shard] = cursor;
            return Task.FromResult(cursor);
        }
    }

    public Task<bool> TryAdvanceCursorAsync(
        ProcessStepDispatchCursorRecord cursor,
        long nextDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_cursors.TryGetValue(cursor.Shard, out var current))
            {
                return Task.FromResult(false);
            }

            current.NextDueBucket = Math.Max(current.NextDueBucket, nextDueBucket);
            current.UpdatedAt = timestamp;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryRewindCursorAsync(
        int shard,
        long nextDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_cursors.TryGetValue(shard, out var current))
            {
                current = new ProcessStepDispatchCursorRecord
                {
                    id = ProcessStepDispatchIntentKeys.CreateCursorId(shard),
                    PartitionKey = ProcessStepDispatchIntentKeys.CreateCursorPartitionKey(shard),
                    Shard = shard,
                    NextDueBucket = nextDueBucket,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                };
                _cursors[shard] = current;
            }
            else if (nextDueBucket < current.NextDueBucket)
            {
                current.NextDueBucket = nextDueBucket;
                current.UpdatedAt = timestamp;
            }

            return Task.FromResult(true);
        }
    }

    public Task<ProcessStepDispatchBacklogSnapshot> GetBacklogSnapshotAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            BacklogSnapshotCalls++;
            var active = _intents.Values
                .Where(record => record.Status is ProcessStepDispatchStatus.Pending
                    or ProcessStepDispatchStatus.RetryableDispatchError
                    or ProcessStepDispatchStatus.Dispatching)
                .OrderBy(record => record.CreatedAt)
                .ToList();
            return Task.FromResult(new ProcessStepDispatchBacklogSnapshot(
                active.Count,
                active.FirstOrDefault()?.CreatedAt));
        }
    }

    private bool TryGetLeased(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        out ProcessStepDispatchIntentRecord current)
    {
        current = null!;
        if (!_intents.TryGetValue((record.Id, record.PartitionKey), out var existing) ||
            !string.Equals(existing.LeaseId, leaseId, StringComparison.Ordinal))
        {
            return false;
        }

        current = existing;
        return true;
    }

    private static bool IsDue(
        ProcessStepDispatchIntentRecord record,
        DateTimeOffset now) =>
        ((record.Status is ProcessStepDispatchStatus.Pending or ProcessStepDispatchStatus.RetryableDispatchError) &&
            record.NextAttemptAt <= now) ||
        record.Status is ProcessStepDispatchStatus.Dispatching &&
            record.LockedUntil.HasValue &&
            record.LockedUntil.Value <= now;
}
