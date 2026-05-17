using System.Globalization;
using Azure.Messaging.ServiceBus;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging;

namespace Backend.Services.ProcessStepQueue;

public sealed class ProcessStepDispatchService
{
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessStepQueueWriter _processStepQueueWriter;
    private readonly ProcessStepDispatchOptions _options;
    private readonly ProcessStepDispatchTelemetry _telemetry;
    private readonly ILogger<ProcessStepDispatchService> _logger;

    public ProcessStepDispatchService(
        IProcessStepRepository processStepRepository,
        IProcessStepQueueWriter processStepQueueWriter,
        ProcessStepDispatchOptions options,
        ProcessStepDispatchTelemetry telemetry,
        ILogger<ProcessStepDispatchService> logger)
    {
        _processStepRepository = processStepRepository;
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

    public async Task<int> DrainDueDispatchesAsync(
        int maxItemCount,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var dueDispatches = await _processStepRepository.GetDueStepDispatchesAsync(
            now,
            Math.Max(1, maxItemCount),
            cancellationToken);

        var dispatchedCount = 0;
        foreach (var step in dueDispatches)
        {
            if (await TryDispatchAsync(step, cancellationToken))
            {
                dispatchedCount++;
            }
        }

        await RefreshBacklogMetricsAsync(cancellationToken);
        return dispatchedCount;
    }

    public async Task<bool> TryDispatchAsync(
        ProcessStepRecord step,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || step.Status is not ProcessStepStatus.Pending)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var leaseId = Guid.NewGuid().ToString("N");
        (bool claimed, ProcessStepRecord? claimedStep) claimedDispatch;
        try
        {
            claimedDispatch = await _processStepRepository.TryClaimStepDispatchAsync(
                step,
                leaseId,
                now,
                EffectiveLeaseDuration,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Could not claim process-step dispatch for step {ProcessStepId}; the durable intent remains pending.",
                step.Id);
            return false;
        }

        if (!claimedDispatch.claimed || claimedDispatch.claimedStep is null)
        {
            return false;
        }

        var claimedStep = claimedDispatch.claimedStep;

        try
        {
            await _processStepQueueWriter.EnqueueAsync(
                CreateMessage(claimedStep),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var failedAt = DateTimeOffset.UtcNow;
            var errorKind = ClassifyError(exception);
            var nextAttemptAt = CalculateNextAttemptAt(
                claimedStep.Dispatch?.AttemptCount ?? 1,
                failedAt);

            try
            {
                await _processStepRepository.MarkStepDispatchRetryableErrorAsync(
                    claimedStep,
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
                    "Could not record dispatch retry state for process step {ProcessStepId}; the dispatch lease will expire for retry.",
                    claimedStep.Id);
            }

            _telemetry.RecordFailure(errorKind.ToString());
            _logger.LogWarning(
                exception,
                "Could not dispatch process-step message for step {ProcessStepId}; next attempt is scheduled for {NextAttemptAt}.",
                claimedStep.Id,
                nextAttemptAt);

            return false;
        }

        var dispatchedAt = DateTimeOffset.UtcNow;
        bool marked;
        try
        {
            marked = await _processStepRepository.MarkStepDispatchSucceededAsync(
                claimedStep,
                leaseId,
                dispatchedAt,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Sent process-step dispatch message for step {ProcessStepId}, but could not mark dispatch as completed.",
                claimedStep.Id);
            return false;
        }

        if (!marked)
        {
            _logger.LogWarning(
                "Sent process-step dispatch message for step {ProcessStepId}, but could not mark dispatch as completed.",
                claimedStep.Id);
            return false;
        }

        _telemetry.RecordLatency(
            claimedStep.Dispatch?.CreatedAt ?? claimedStep.CreatedAt,
            dispatchedAt);

        return true;
    }

    public async Task RefreshBacklogMetricsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = await _processStepRepository.GetDispatchBacklogSnapshotAsync(
            now,
            cancellationToken);
        _telemetry.RecordBacklog(snapshot, now);
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

    private DateTimeOffset CalculateNextAttemptAt(
        int attemptCount,
        DateTimeOffset failedAt)
    {
        var exponent = Math.Clamp(attemptCount - 1, 0, 10);
        var delayMs = EffectiveRetryBaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedDelayMs = Math.Min(delayMs, EffectiveRetryMaxDelay.TotalMilliseconds);
        return failedAt.AddMilliseconds(Math.Max(1, cappedDelayMs));
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
