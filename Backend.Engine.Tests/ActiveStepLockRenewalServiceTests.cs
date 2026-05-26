using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Engine.Debugging;
using Backend.Interfaces.Queues;
using Backend.Models;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ActiveStepLockRenewalServiceTests
{
    [Test]
    public async Task Active_step_renews_message_lock_while_result_is_pending()
    {
        var delay = new ManualDelay();
        var telemetry = new ProcessStepLockRenewalTelemetry();
        var queueMessage = new ProcessStepQueueMessageSpy
        {
            LockedUntil = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        var context = CreateContext(queueMessage);
        var service = CreateService(delay, telemetry);

        await using var renewal = service.Start(context);
        renewal.ShouldNotBeNull();

        await WaitUntilAsync(() => delay.DelayCount == 1);
        delay.CompleteNext();

        await WaitUntilAsync(() => queueMessage.RenewCount == 1);
        telemetry.RecordedRenewals.ShouldBe(1);
    }

    [Test]
    public async Task Renewal_stops_when_message_is_completed()
    {
        var delay = new ManualDelay();
        var queueMessage = new ProcessStepQueueMessageSpy
        {
            LockedUntil = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        var context = CreateContext(queueMessage);
        var service = CreateService(delay, new ProcessStepLockRenewalTelemetry());
        context.AttachLockRenewal(service.Start(context)!);

        await WaitUntilAsync(() => delay.DelayCount == 1);

        await context.CompleteMessageAsync(CancellationToken.None);

        queueMessage.CompleteCount.ShouldBe(1);
        queueMessage.RenewCount.ShouldBe(0);
    }

    [Test]
    public async Task Renewal_stops_when_message_is_abandoned()
    {
        var delay = new ManualDelay();
        var queueMessage = new ProcessStepQueueMessageSpy
        {
            LockedUntil = DateTimeOffset.UtcNow.AddSeconds(10)
        };
        var context = CreateContext(queueMessage);
        var service = CreateService(delay, new ProcessStepLockRenewalTelemetry());
        context.AttachLockRenewal(service.Start(context)!);

        await WaitUntilAsync(() => delay.DelayCount == 1);

        await context.AbandonMessageAsync(CancellationToken.None);

        queueMessage.AbandonCount.ShouldBe(1);
        queueMessage.RenewCount.ShouldBe(0);
    }

    [Test]
    public async Task Renewal_failure_records_failure_and_suspected_lock_loss()
    {
        var delay = new ManualDelay();
        var telemetry = new ProcessStepLockRenewalTelemetry();
        var queueMessage = new ProcessStepQueueMessageSpy
        {
            LockedUntil = DateTimeOffset.UtcNow.AddMilliseconds(-1),
            RenewException = new InvalidOperationException("renew failed")
        };
        var context = CreateContext(queueMessage);
        var service = CreateService(delay, telemetry);

        await using var renewal = service.Start(context);
        renewal.ShouldNotBeNull();

        await WaitUntilAsync(() => queueMessage.RenewCount == 1);
        await WaitUntilAsync(() => telemetry.RecordedRenewalFailures == 1);
        telemetry.RecordedLockLostSuspected.ShouldBe(1);
        telemetry.RecordedActiveRenewalDurations.ShouldBe(1);
    }

    [Test]
    public void Telemetry_emits_lock_renewal_metrics()
    {
        var longMeasurements = new List<(string Instrument, long Value)>();
        var doubleMeasurements = new List<(string Instrument, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProcessStepLockRenewalTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            longMeasurements.Add((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            doubleMeasurements.Add((instrument.Name, measurement)));
        listener.Start();

        var telemetry = new ProcessStepLockRenewalTelemetry();
        telemetry.RecordRenewal();
        telemetry.RecordRenewalFailure(new InvalidOperationException("failure"));
        telemetry.RecordLockLostSuspected("test");
        telemetry.RecordActiveRenewalDuration(TimeSpan.FromMilliseconds(25), "stopped");

        longMeasurements.ShouldContain(measurement =>
            measurement.Instrument == "process_step_message_lock_renewals_total" &&
            measurement.Value == 1);
        longMeasurements.ShouldContain(measurement =>
            measurement.Instrument == "process_step_message_lock_renewal_failures_total" &&
            measurement.Value == 1);
        longMeasurements.ShouldContain(measurement =>
            measurement.Instrument == "process_step_message_lock_lost_suspected_total" &&
            measurement.Value == 1);
        doubleMeasurements.ShouldContain(measurement =>
            measurement.Instrument == "process_step_message_lock_active_renewal_duration_ms" &&
            measurement.Value >= 0);
    }

    private static ActiveStepLockRenewalService CreateService(
        IAsyncDelay delay,
        ProcessStepLockRenewalTelemetry telemetry) =>
        new(
            new ProcessStepQueueLockRenewalOptions
            {
                ActiveMessageLockRenewalEnabled = true,
                RenewalLeadTimeSeconds = 1,
                MaxActiveRenewalDurationMinutes = 30
            },
            delay,
            telemetry,
            new ApplicationLifetimeStub(),
            NullLogger<ActiveStepLockRenewalService>.Instance);

    private static StepExecutionContext CreateContext(ProcessStepQueueMessageSpy queueMessage)
    {
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var node = new ExecutableBlueprintNode
        {
            Id = nodeId,
            Name = "Plugin",
            TaskDefinitionId = 10,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedTaskVersionId = 100,
            ResolvedVersion = new Version(1, 0, 0),
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = "Input",
                Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                OutboundBindings = []
            },
            OutputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal),
            InboundBindings = [],
            DesignTimeInputOverrides = []
        };
        var blueprint = new ExecutableBlueprint
        {
            Id = 101,
            DefinitionId = 10,
            VersionNumber = 1,
            Name = "Execution Blueprint",
            InitialNode = node,
            AllNodes = [node]
        };
        var message = new ProcessStepInitialisationMessage
        {
            ProcessId = processId,
            ProcessStepId = stepId,
            ExecutableBlueprintId = blueprint.Id,
            BlueprintNodeId = nodeId
        };
        queueMessage.Payload = message;
        return new StepExecutionContext(
            message,
            NullLogger<StepExecutionContext>.Instance,
            queueMessage,
            blueprint);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ProcessStepQueueMessageSpy : IProcessStepQueueMessage
    {
        public int RenewCount { get; private set; }
        public int CompleteCount { get; private set; }
        public int AbandonCount { get; private set; }
        public string MessageId { get; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow.AddSeconds(-5);
        public DateTimeOffset LockedUntil { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public ProcessStepInitialisationMessage Payload { get; set; } = new();
        public Exception? RenewException { get; set; }

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            CompleteCount++;
            return Task.CompletedTask;
        }

        public Task AbandonAsync(CancellationToken cancellationToken)
        {
            AbandonCount++;
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset> RenewLockAsync(CancellationToken cancellationToken)
        {
            RenewCount++;
            if (RenewException is not null)
            {
                throw RenewException;
            }

            LockedUntil = DateTimeOffset.UtcNow.AddSeconds(10);
            return Task.FromResult(LockedUntil);
        }
    }

    private sealed class ManualDelay : IAsyncDelay
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _pending;
        private int _delayCount;

        public int DelayCount => Volatile.Read(ref _delayCount);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _pending = pending;
                Interlocked.Increment(ref _delayCount);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                pending.TrySetCanceled(cancellationToken);
            }
            else
            {
                cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                    pending);
            }

            return pending.Task;
        }

        public void CompleteNext()
        {
            TaskCompletionSource? pending;
            lock (_sync)
            {
                pending = _pending;
                _pending = null;
            }

            pending.ShouldNotBeNull();
            pending.TrySetResult();
        }
    }

    private sealed class ApplicationLifetimeStub : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
