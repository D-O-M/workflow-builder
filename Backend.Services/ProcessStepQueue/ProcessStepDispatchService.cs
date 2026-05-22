using System.Globalization;
using Azure.Messaging.ServiceBus;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging;

namespace Backend.Services.ProcessStepQueue;

public sealed class ProcessStepDispatchService : IProcessStepDispatcher
{
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessStepDispatchIntentRepository _dispatchIntentRepository;
    private readonly IProcessStepQueueWriter _processStepQueueWriter;
    private readonly ProcessStepDispatchOptions _options;
    private readonly ProcessStepDispatchTelemetry _telemetry;
    private readonly ILogger<ProcessStepDispatchService> _logger;

    public ProcessStepDispatchService(
        IProcessStepRepository processStepRepository,
        IProcessStepDispatchIntentRepository dispatchIntentRepository,
        IProcessStepQueueWriter processStepQueueWriter,
        ProcessStepDispatchOptions options,
        ProcessStepDispatchTelemetry telemetry,
        ILogger<ProcessStepDispatchService> logger)
    {
        _processStepRepository = processStepRepository;
        _dispatchIntentRepository = dispatchIntentRepository;
        _processStepQueueWriter = processStepQueueWriter;
        _options = options;
        _telemetry = telemetry;
        _logger = logger;
    }

    public static ProcessStepDispatchState CreatePendingDispatchState(DateTimeOffset timestamp) =>
        new()
        {
            Status = ProcessStepDispatchStatus.Pending,
            MessageId = null,
            AttemptCount = 0,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            NextAttemptAt = timestamp
        };

    public void EnsureDispatchAvailableForExecution()
    {
        if (!_options.Enabled)
        {
            throw new ProcessStepDispatchDisabledException();
        }
    }

    public async Task<ProcessStepDispatchIntentRecord> EnsureDispatchIntentAsync(
        ProcessStepRecord step,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        EnsureDispatchAvailableForExecution();

        if (step.Status is not ProcessStepStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Cannot create a dispatch intent for terminal or claimed process step {step.Id}.");
        }

        var intent = await TryLoadActiveIntentAsync(step, cancellationToken);
        if (intent is not null)
        {
            return intent;
        }

        var now = DateTimeOffset.UtcNow;
        var created = await CreateIntentAsync(
            step,
            nextAttemptAt: timestamp,
            attemptCount: step.Dispatch?.AttemptCount ?? 0,
            now,
            cancellationToken);

        await UpdateStepDispatchProjectionAsync(
            step,
            ToDispatchState(created, ProcessStepDispatchStatus.Pending, created.NextAttemptAt),
            cancellationToken);

        return created;
    }

    public async Task<int> DrainDueDispatchesAsync(
        int maxItemCount,
        CancellationToken cancellationToken)
    {
        EnsureDispatchAvailableForExecution();

        var now = DateTimeOffset.UtcNow;
        var shardCount = EffectiveShardCount;
        var currentBucket = CalculateDueBucket(now, EffectiveDueBucketDuration);
        var remaining = Math.Max(1, maxItemCount);
        var dispatchedCount = 0;

        for (var shard = 0; shard < shardCount && remaining > 0; shard++)
        {
            var cursor = await _dispatchIntentRepository.GetOrCreateCursorAsync(
                shard,
                currentBucket,
                now,
                cancellationToken);

            var bucketsScanned = 0;
            while (cursor.NextDueBucket <= currentBucket &&
                   bucketsScanned < EffectiveMaxBucketsPerPoll &&
                   remaining > 0)
            {
                var dueBucket = cursor.NextDueBucket;
                var requestedCount = remaining;
                var dueIntents = await _dispatchIntentRepository.GetDueIntentsAsync(
                    dueBucket,
                    shard,
                    requestedCount,
                    now,
                    cancellationToken);

                foreach (var intent in dueIntents)
                {
                    remaining--;
                    if (await TryDispatchIntentAsync(intent, cancellationToken))
                    {
                        dispatchedCount++;
                    }

                    if (remaining == 0)
                    {
                        break;
                    }
                }

                if (dueIntents.Count >= requestedCount)
                {
                    break;
                }

                var advanced = await _dispatchIntentRepository.TryAdvanceCursorAsync(
                    cursor,
                    dueBucket + 1,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                if (!advanced)
                {
                    break;
                }

                cursor.NextDueBucket = dueBucket + 1;
                bucketsScanned++;
            }
        }

        await RefreshBacklogMetricsAsync(cancellationToken);
        return dispatchedCount;
    }

    public async Task<bool> TryDispatchAsync(
        ProcessStepRecord step,
        CancellationToken cancellationToken)
    {
        EnsureDispatchAvailableForExecution();

        if (step.Status is not ProcessStepStatus.Pending)
        {
            return false;
        }

        ProcessStepDispatchIntentRecord intent;
        try
        {
            intent = await EnsureDispatchIntentAsync(
                step,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Could not create process-step dispatch intent for step {ProcessStepId}; the durable step remains pending.",
                step.Id);
            throw;
        }

        return await TryDispatchIntentAsync(intent, cancellationToken);
    }

    public async Task RefreshBacklogMetricsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = await _dispatchIntentRepository.GetBacklogSnapshotAsync(
            now,
            cancellationToken);
        _telemetry.RecordBacklog(snapshot, now);
    }

    public static long CalculateDueBucket(DateTimeOffset timestamp, TimeSpan dueBucketDuration)
        => ProcessStepDispatchSchedule.CalculateDueBucket(timestamp, dueBucketDuration);

    public static int CalculateShard(string processStepId, int shardCount)
        => ProcessStepDispatchSchedule.CalculateShard(processStepId, shardCount);

    public static string CreateIntentId(
        string processStepId,
        long dueBucket,
        int attemptCount) =>
        ProcessStepDispatchSchedule.CreateIntentId(processStepId, dueBucket, attemptCount);

    private async Task<bool> TryDispatchIntentAsync(
        ProcessStepDispatchIntentRecord intent,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseId = Guid.NewGuid().ToString("N");
        (bool claimed, ProcessStepDispatchIntentRecord? record) claimedDispatch;
        try
        {
            claimedDispatch = await _dispatchIntentRepository.TryClaimIntentAsync(
                intent,
                leaseId,
                now,
                EffectiveLeaseDuration,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Could not claim process-step dispatch intent {DispatchIntentId}; the intent remains pending.",
                intent.Id);
            return false;
        }

        if (!claimedDispatch.claimed || claimedDispatch.record is null)
        {
            return false;
        }

        var claimedIntent = claimedDispatch.record;
        ProcessStepRecord step;
        try
        {
            step = await _processStepRepository.GetProcessStepById(
                Guid.Parse(claimedIntent.ProcessStepId),
                Guid.Parse(claimedIntent.ProcessId),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                         (exception is KeyNotFoundException ||
                                          exception.GetType().Name.StartsWith("EntityNotFoundException", StringComparison.Ordinal)))
        {
            await MarkIntentSkippedAsync(claimedIntent, leaseId, cancellationToken);
            return false;
        }

        if (step.Status is not ProcessStepStatus.Pending ||
            !IsActiveIntent(step, claimedIntent))
        {
            await MarkIntentSkippedAsync(claimedIntent, leaseId, cancellationToken);
            return false;
        }

        try
        {
            await _processStepQueueWriter.EnqueueAsync(
                CreateMessage(step),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var failedAt = DateTimeOffset.UtcNow;
            var errorKind = ClassifyError(exception);
            var nextAttemptAt = CalculateNextAttemptAt(
                claimedIntent.AttemptCount,
                failedAt);
            ProcessStepDispatchIntentRecord? retryIntent = null;

            try
            {
                retryIntent = await CreateIntentAsync(
                    step,
                    nextAttemptAt,
                    claimedIntent.AttemptCount,
                    failedAt,
                    cancellationToken);

                await UpdateStepDispatchProjectionAsync(
                    step,
                    ToRetryDispatchState(retryIntent, errorKind, exception.Message, failedAt),
                    cancellationToken);

                await _dispatchIntentRepository.MarkIntentRetryableErrorAsync(
                    claimedIntent,
                    leaseId,
                    errorKind,
                    exception.Message,
                    failedAt,
                    nextAttemptAt,
                    cancellationToken);
            }
            catch (Exception markException) when (markException is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    markException,
                    "Could not record retry state for dispatch intent {DispatchIntentId}; the dispatch lease will expire for retry.",
                    claimedIntent.Id);
            }

            _telemetry.RecordFailure(errorKind.ToString());
            _logger.LogWarning(
                exception,
                "Could not dispatch process-step message for step {ProcessStepId}; next intent {RetryIntentId} is scheduled for {NextAttemptAt}.",
                claimedIntent.ProcessStepId,
                retryIntent?.Id,
                nextAttemptAt);

            return false;
        }

        var dispatchedAt = DateTimeOffset.UtcNow;
        bool marked;
        try
        {
            marked = await _dispatchIntentRepository.MarkIntentDispatchedAsync(
                claimedIntent,
                leaseId,
                dispatchedAt,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Sent process-step dispatch message for step {ProcessStepId}, but could not mark dispatch intent {DispatchIntentId} as completed.",
                claimedIntent.ProcessStepId,
                claimedIntent.Id);
            return false;
        }

        if (!marked)
        {
            _logger.LogWarning(
                "Sent process-step dispatch message for step {ProcessStepId}, but could not mark dispatch intent {DispatchIntentId} as completed.",
                claimedIntent.ProcessStepId,
                claimedIntent.Id);
            return false;
        }

        await UpdateStepDispatchProjectionAsync(
            step,
            ToDispatchedState(claimedIntent, dispatchedAt),
            cancellationToken);

        _telemetry.RecordLatency(
            claimedIntent.CreatedAt,
            dispatchedAt);

        return true;
    }

    private async Task<ProcessStepDispatchIntentRecord?> TryLoadActiveIntentAsync(
        ProcessStepRecord step,
        CancellationToken cancellationToken)
    {
        if (step.Dispatch?.ActiveIntentId is null ||
            step.Dispatch.DueBucket is not long dueBucket ||
            step.Dispatch.Shard is not int shard)
        {
            return null;
        }

        var partitionKey = ProcessStepDispatchIntentKeys.CreateDuePartitionKey(dueBucket, shard);
        return await _dispatchIntentRepository.GetIntentAsync(
            step.Dispatch.ActiveIntentId,
            partitionKey,
            cancellationToken);
    }

    private async Task<ProcessStepDispatchIntentRecord> CreateIntentAsync(
        ProcessStepRecord step,
        DateTimeOffset nextAttemptAt,
        int attemptCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var dueBucket = CalculateScheduledDueBucket(nextAttemptAt, now);
        var shard = CalculateShard(step.Id, EffectiveShardCount);
        var partitionKey = ProcessStepDispatchIntentKeys.CreateDuePartitionKey(dueBucket, shard);
        var intent = new ProcessStepDispatchIntentRecord
        {
            id = CreateIntentId(step.Id, dueBucket, attemptCount),
            PartitionKey = partitionKey,
            ProcessStepId = step.Id,
            ProcessId = step.ProcessId,
            BlueprintId = step.BlueprintId,
            BlueprintNodeId = step.BlueprintNodeId,
            Status = ProcessStepDispatchStatus.Pending,
            DueBucket = dueBucket,
            Shard = shard,
            NextAttemptAt = nextAttemptAt,
            AttemptCount = attemptCount,
            CreatedAt = now,
            UpdatedAt = now
        };

        var (_, record) = await _dispatchIntentRepository.CreateIntentIfNotExistsAsync(
            intent,
            cancellationToken);
        var cursorReady = await _dispatchIntentRepository.TryRewindCursorAsync(
            shard,
            dueBucket,
            now,
            cancellationToken);
        if (!cursorReady)
        {
            throw new InvalidOperationException(
                $"Could not position dispatch cursor for shard {shard} at due bucket {dueBucket}.");
        }

        return record;
    }

    private long CalculateScheduledDueBucket(
        DateTimeOffset dueAt,
        DateTimeOffset now)
    {
        var duration = EffectiveDueBucketDuration;
        return Math.Max(
            CalculateDueBucket(dueAt, duration),
            CalculateDueBucket(now, duration) + 1);
    }

    private async Task UpdateStepDispatchProjectionAsync(
        ProcessStepRecord step,
        ProcessStepDispatchState dispatch,
        CancellationToken cancellationToken)
    {
        step.Dispatch = dispatch;
        if (!Guid.TryParse(step.Id, out var stepId) ||
            !Guid.TryParse(step.ProcessId, out var processId))
        {
            return;
        }

        var updated = await _processStepRepository.TryUpdateStepDispatchAsync(
            stepId,
            processId,
            dispatch,
            cancellationToken);
        if (!updated)
        {
            _logger.LogDebug(
                "Could not update process-step dispatch projection for step {ProcessStepId}; scheduler intent remains authoritative.",
                step.Id);
        }
    }

    private async Task MarkIntentSkippedAsync(
        ProcessStepDispatchIntentRecord intent,
        string leaseId,
        CancellationToken cancellationToken)
    {
        var skippedAt = DateTimeOffset.UtcNow;
        await _dispatchIntentRepository.MarkIntentSkippedTerminalAsync(
            intent,
            leaseId,
            skippedAt,
            cancellationToken);
    }

    private TimeSpan EffectiveLeaseDuration =>
        _options.LeaseDuration > TimeSpan.Zero
            ? _options.LeaseDuration
            : TimeSpan.FromSeconds(30);

    private TimeSpan EffectiveRetryBaseDelay =>
        _options.RetryBaseDelay > TimeSpan.Zero
            ? _options.RetryBaseDelay
            : TimeSpan.FromSeconds(1);

    private TimeSpan EffectiveRetryMaxDelay =>
        _options.RetryMaxDelay > TimeSpan.Zero
            ? _options.RetryMaxDelay
            : TimeSpan.FromMinutes(1);

    private int EffectiveShardCount =>
        Math.Clamp(_options.ShardCount, 1, 1024);

    private TimeSpan EffectiveDueBucketDuration =>
        _options.DueBucketDuration > TimeSpan.Zero
            ? _options.DueBucketDuration
            : TimeSpan.FromSeconds(5);

    private int EffectiveMaxBucketsPerPoll =>
        Math.Clamp(_options.MaxBucketsPerPoll, 1, 1024);

    private DateTimeOffset CalculateNextAttemptAt(
        int attemptCount,
        DateTimeOffset failedAt)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 10);
        var delayMs = EffectiveRetryBaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedDelayMs = Math.Min(delayMs, EffectiveRetryMaxDelay.TotalMilliseconds);
        return failedAt.AddMilliseconds(Math.Max(1, cappedDelayMs));
    }

    private static bool IsActiveIntent(
        ProcessStepRecord step,
        ProcessStepDispatchIntentRecord intent) =>
        string.IsNullOrWhiteSpace(step.Dispatch?.ActiveIntentId) ||
        string.Equals(step.Dispatch.ActiveIntentId, intent.Id, StringComparison.Ordinal);

    private static ProcessStepDispatchState ToDispatchState(
        ProcessStepDispatchIntentRecord intent,
        ProcessStepDispatchStatus status,
        DateTimeOffset nextAttemptAt) =>
        new()
        {
            ActiveIntentId = intent.Id,
            DueBucket = intent.DueBucket,
            Shard = intent.Shard,
            Status = status,
            MessageId = intent.ProcessStepId,
            AttemptCount = intent.AttemptCount,
            CreatedAt = intent.CreatedAt,
            UpdatedAt = intent.UpdatedAt,
            NextAttemptAt = nextAttemptAt,
            DispatchingStartedAt = intent.DispatchingStartedAt,
            LockedUntil = intent.LockedUntil,
            LeaseId = intent.LeaseId,
            DispatchedAt = intent.DispatchedAt,
            LastErrorKind = intent.LastErrorKind,
            LastErrorMessage = intent.LastErrorMessage,
            LastErrorAt = intent.LastErrorAt
        };

    private static ProcessStepDispatchState ToRetryDispatchState(
        ProcessStepDispatchIntentRecord retryIntent,
        ProcessStepDispatchErrorKind errorKind,
        string errorMessage,
        DateTimeOffset failedAt)
    {
        var dispatch = ToDispatchState(
            retryIntent,
            ProcessStepDispatchStatus.RetryableDispatchError,
            retryIntent.NextAttemptAt);
        dispatch.LastErrorKind = errorKind;
        dispatch.LastErrorMessage = errorMessage;
        dispatch.LastErrorAt = failedAt;
        dispatch.UpdatedAt = failedAt;
        return dispatch;
    }

    private static ProcessStepDispatchState ToDispatchedState(
        ProcessStepDispatchIntentRecord intent,
        DateTimeOffset dispatchedAt)
    {
        var dispatch = ToDispatchState(
            intent,
            ProcessStepDispatchStatus.Dispatched,
            dispatchedAt);
        dispatch.AttemptCount = intent.AttemptCount;
        dispatch.UpdatedAt = dispatchedAt;
        dispatch.DispatchedAt = dispatchedAt;
        dispatch.NextAttemptAt = null;
        dispatch.DispatchingStartedAt = null;
        dispatch.LockedUntil = null;
        dispatch.LeaseId = null;
        dispatch.LastErrorKind = null;
        dispatch.LastErrorMessage = null;
        dispatch.LastErrorAt = null;
        return dispatch;
    }

    private static ProcessStepDispatchErrorKind ClassifyError(Exception exception) =>
        exception is ServiceBusException { IsTransient: false }
            ? ProcessStepDispatchErrorKind.DispatchFailed
            : ProcessStepDispatchErrorKind.RetryableServiceBusError;

    private static ProcessStepInitialisationMessage CreateMessage(ProcessStepRecord step) =>
        new()
        {
            ProcessId = Guid.Parse(step.ProcessId),
            ExecutableBlueprintId = int.Parse(step.BlueprintId, CultureInfo.InvariantCulture),
            ProcessStepId = Guid.Parse(step.Id),
            BlueprintNodeId = Guid.Parse(step.BlueprintNodeId)
        };
}
