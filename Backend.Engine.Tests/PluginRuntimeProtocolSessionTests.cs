using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Backend.Engine;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginRuntimeProtocolSessionTests
{
    [Test]
    public async Task Ready_frame_marks_session_ready()
    {
        var session = CreateSession(out _, out _, out _);

        var disposition = await session.ProcessOutputLineAsync(
            PluginProcessMessageSerializer.Serialize(PluginProcessMessageSerializer.Ready()),
            CancellationToken.None);

        disposition.ShouldBe(PluginRuntimeProtocolLineDisposition.Ready);
        session.IsReady.ShouldBeTrue();
    }

    [Test]
    public async Task Result_frame_for_tracked_job_enqueues_finalization_work()
    {
        var session = CreateSession(out var queue, out _, out _);
        var result = CreateResult(Guid.NewGuid());
        session.TrackQueuedJob(result.JobId);

        var disposition = await session.ProcessOutputLineAsync(
            PluginProcessMessageSerializer.Serialize(PluginProcessMessageSerializer.Result(result)),
            CancellationToken.None);

        disposition.ShouldBe(PluginRuntimeProtocolLineDisposition.ResultQueued);
        await using var reader = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).ShouldBeTrue();
        reader.Current.Result.JobId.ShouldBe(result.JobId);
        session.InFlightJobCount.ShouldBe(0);
        queue.Complete();
    }

    [Test]
    public async Task Malformed_output_records_protocol_error_without_throwing()
    {
        var session = CreateSession(out _, out var telemetry, out var healthEvents);

        var disposition = await session.ProcessOutputLineAsync("not-json", CancellationToken.None);

        disposition.ShouldBe(PluginRuntimeProtocolLineDisposition.Malformed);
        telemetry.RecordedProtocolErrors.ShouldBe(1);
        var healthEvent = await ReadHealthEventAsync(healthEvents);
        healthEvent.EventType.ShouldBe(PluginAgentHealthEventType.ProtocolMalformed);
    }

    [Test]
    public async Task Unknown_job_result_is_dropped_and_recorded()
    {
        var session = CreateSession(out var queue, out var telemetry, out var healthEvents);
        var result = CreateResult(Guid.NewGuid());

        var disposition = await session.ProcessOutputLineAsync(
            PluginProcessMessageSerializer.Serialize(PluginProcessMessageSerializer.Result(result)),
            CancellationToken.None);

        disposition.ShouldBe(PluginRuntimeProtocolLineDisposition.UnknownJobResult);
        telemetry.RecordedUnknownJobResults.ShouldBe(1);
        queue.Depth.ShouldBe(0);
        var healthEvent = await ReadHealthEventAsync(healthEvents);
        healthEvent.EventType.ShouldBe(PluginAgentHealthEventType.ResultForUnknownJob);
        healthEvent.ProcessStepIds.ShouldContain(result.JobId);
    }

    [Test]
    public async Task Heartbeat_frame_records_heartbeat_metric()
    {
        var session = CreateSession(out _, out var telemetry, out _);

        var disposition = await session.ProcessOutputLineAsync(
            PluginProcessMessageSerializer.Serialize(PluginProcessMessageSerializer.Heartbeat()),
            CancellationToken.None);

        disposition.ShouldBe(PluginRuntimeProtocolLineDisposition.Heartbeat);
        telemetry.RecordedHeartbeats.ShouldBe(1);
    }

    [Test]
    public void Process_exit_before_ready_reports_startup_failure()
    {
        var session = CreateSession(out _, out _, out _);

        var summary = session.RecordProcessExit(exitCode: 1);

        summary.ExitedBeforeReady.ShouldBeTrue();
        summary.InFlightJobCount.ShouldBe(0);
    }

    [Test]
    public void Process_exit_with_inflight_jobs_records_recovery_metric()
    {
        var session = CreateSession(out _, out var telemetry, out _);
        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();
        session.TrackQueuedJob(firstJobId);
        session.TrackQueuedJob(secondJobId);

        var summary = session.RecordProcessExit(exitCode: 1);

        summary.InFlightJobCount.ShouldBe(2);
        summary.InFlightJobIds.ShouldContain(firstJobId);
        summary.InFlightJobIds.ShouldContain(secondJobId);
        telemetry.RecordedInFlightJobsOnExit.ShouldBe(2);
        session.InFlightJobCount.ShouldBe(0);
    }

    [Test]
    public void Shutdown_process_exit_with_inflight_jobs_returns_ids_without_recovery_metric()
    {
        var session = CreateSession(out _, out var telemetry, out _);
        var jobId = Guid.NewGuid();
        session.TrackQueuedJob(jobId);

        var summary = session.RecordProcessExit(exitCode: 0, shutdownRequested: true);

        summary.InFlightJobIds.ShouldContain(jobId);
        telemetry.RecordedInFlightJobsOnExit.ShouldBe(0);
        session.InFlightJobCount.ShouldBe(0);
    }

    [Test]
    public async Task Closed_finalization_queue_returns_queue_closed_disposition()
    {
        var session = CreateSession(out var queue, out _, out var healthEvents);
        var result = CreateResult(Guid.NewGuid());
        session.TrackQueuedJob(result.JobId);
        queue.Complete();

        var disposition = await session.ProcessOutputLineAsync(
            PluginProcessMessageSerializer.Serialize(PluginProcessMessageSerializer.Result(result)),
            CancellationToken.None);

        disposition.ShouldBe(PluginRuntimeProtocolLineDisposition.QueueClosed);
        var healthEvent = await ReadHealthEventAsync(healthEvents);
        healthEvent.EventType.ShouldBe(PluginAgentHealthEventType.FinalizationQueueClosed);
        healthEvent.ProcessStepIds.ShouldContain(result.JobId);
    }

    private static PluginRuntimeProtocolSession CreateSession(
        out PluginResultFinalizationQueue queue,
        out PluginRuntimeAgentTelemetry telemetry,
        out PluginAgentHealthEventBus healthEvents)
    {
        queue = new PluginResultFinalizationQueue(
            new PluginRuntimeOptions
            {
                ResultFinalizationQueueCapacity = 4,
                ResultFinalizationQueueFullMode = BoundedChannelFullMode.Wait
            },
            new PluginResultFinalizationTelemetry());
        telemetry = new PluginRuntimeAgentTelemetry();
        healthEvents = new PluginAgentHealthEventBus();
        return new PluginRuntimeProtocolSession(
            CreateIdentity(),
            queue,
            telemetry,
            healthEvents,
            NullLogger.Instance);
    }

    private static async Task<PluginAgentHealthEvent> ReadHealthEventAsync(
        PluginAgentHealthEventBus healthEvents)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = healthEvents.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).ShouldBeTrue();
        return reader.Current;
    }

    private static PluginAgentIdentity CreateIdentity() =>
        new(
            Guid.NewGuid(),
            TaskVersionId: 101,
            TaskDefinitionId: 10,
            "TestPlugin",
            new Version(1, 0, 0));

    private static PluginExecutionResult CreateResult(Guid jobId) =>
        new(
            jobId,
            Succeeded: true,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);
}
