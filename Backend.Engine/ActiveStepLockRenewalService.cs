using Azure.Messaging.ServiceBus;
using Backend.Engine.Debugging;
using Microsoft.Extensions.Hosting;

namespace Backend.Engine;

public sealed class ActiveStepLockRenewalService
{
    private readonly ProcessStepQueueLockRenewalOptions _options;
    private readonly IAsyncDelay _delay;
    private readonly ProcessStepLockRenewalTelemetry _telemetry;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<ActiveStepLockRenewalService> _logger;

    public ActiveStepLockRenewalService(
        ProcessStepQueueLockRenewalOptions options,
        IAsyncDelay delay,
        ProcessStepLockRenewalTelemetry telemetry,
        IHostApplicationLifetime applicationLifetime,
        ILogger<ActiveStepLockRenewalService> logger)
    {
        _options = options;
        _delay = delay;
        _telemetry = telemetry;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    public IAsyncDisposable? Start(StepExecutionContext context)
    {
        if (!_options.ActiveMessageLockRenewalEnabled)
        {
            return null;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _applicationLifetime.ApplicationStopping);
        var task = Task.Run(
            () => RenewUntilStoppedAsync(context, startedAt, cancellation.Token),
            CancellationToken.None);

        return new ActiveStepLockRenewalHandle(cancellation, task);
    }

    private async Task RenewUntilStoppedAsync(
        StepExecutionContext context,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var outcome = "stopped";
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (HasExceededMaxDuration(startedAt, now))
                {
                    outcome = "max_duration";
                    _telemetry.RecordLockLostSuspected(outcome);
                    _logger.LogWarning(
                        "Stopped renewing process-step message lock for {ProcessStepId} after reaching the configured maximum active renewal duration {MaxRenewalDuration}.",
                        context.ProcessStepId,
                        _options.EffectiveMaxActiveRenewalDuration);
                    return;
                }

                var renewAfter = context.ProcessingWindowExpiresAt - now - _options.EffectiveRenewalLeadTime;
                if (renewAfter > TimeSpan.Zero)
                {
                    await _delay.DelayAsync(renewAfter, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();

                now = DateTimeOffset.UtcNow;
                if (HasExceededMaxDuration(startedAt, now))
                {
                    outcome = "max_duration";
                    _telemetry.RecordLockLostSuspected(outcome);
                    _logger.LogWarning(
                        "Stopped renewing process-step message lock for {ProcessStepId} after reaching the configured maximum active renewal duration {MaxRenewalDuration}.",
                        context.ProcessStepId,
                        _options.EffectiveMaxActiveRenewalDuration);
                    return;
                }

                await context.RenewProcessingWindowAsync(cancellationToken);
                _telemetry.RecordRenewal();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "stopped";
        }
        catch (Exception ex)
        {
            outcome = IsMessageLockLost(ex) ? "lock_lost" : "failure";
            _telemetry.RecordRenewalFailure(ex);
            if (outcome is "lock_lost" || DateTimeOffset.UtcNow >= context.ProcessingWindowExpiresAt)
            {
                _telemetry.RecordLockLostSuspected(outcome);
            }

            _logger.LogWarning(
                ex,
                "Stopped renewing process-step message lock for {ProcessStepId}; broker redelivery and idempotent step claim will recover if the lock was lost.",
                context.ProcessStepId);
        }
        finally
        {
            _telemetry.RecordActiveRenewalDuration(DateTimeOffset.UtcNow - startedAt, outcome);
        }
    }

    private bool HasExceededMaxDuration(DateTimeOffset startedAt, DateTimeOffset now) =>
        now - startedAt >= _options.EffectiveMaxActiveRenewalDuration;

    private static bool IsMessageLockLost(Exception exception) =>
        exception is ServiceBusException { Reason: ServiceBusFailureReason.MessageLockLost };

    private sealed class ActiveStepLockRenewalHandle : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _task;
        private int _disposed;

        public ActiveStepLockRenewalHandle(CancellationTokenSource cancellation, Task task)
        {
            _cancellation = cancellation;
            _task = task;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cancellation.CancelAsync();
            try
            {
                await _task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }
}
