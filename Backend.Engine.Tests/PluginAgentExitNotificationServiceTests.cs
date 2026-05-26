using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginAgentExitNotificationServiceTests
{
    [Test]
    public async Task Non_shutdown_exit_with_inflight_jobs_publishes_health_event()
    {
        var jobId = Guid.NewGuid();
        var service = CreateService(out _, out var healthEvents);

        await service.NotifyProcessExitAsync(
            CreateIdentity(),
            exitCode: 1,
            shutdownRequested: false,
            policyRequestedStop: false,
            new PluginRuntimeProcessExitSummary(ExitedBeforeReady: false, [jobId]),
            CancellationToken.None);

        var healthEvent = await ReadHealthEventAsync(healthEvents);
        healthEvent.EventType.ShouldBe(PluginAgentHealthEventType.AgentExitedWithInFlightJobs);
        healthEvent.ExitCode.ShouldBe(1);
        healthEvent.PolicyRequestedStop.ShouldBeFalse();
        healthEvent.ProcessStepIds.ShouldContain(jobId);
    }

    [Test]
    public async Task Shutdown_exit_does_not_publish_health_event()
    {
        var service = CreateService(out _, out var healthEvents);

        await service.NotifyProcessExitAsync(
            CreateIdentity(),
            exitCode: 0,
            shutdownRequested: true,
            policyRequestedStop: false,
            new PluginRuntimeProcessExitSummary(ExitedBeforeReady: false, [Guid.NewGuid()]),
            CancellationToken.None);

        (await HasHealthEventAsync(healthEvents)).ShouldBeFalse();
    }

    [Test]
    public async Task Agent_exit_before_ready_records_telemetry()
    {
        var service = CreateService(out var telemetry, out var healthEvents);

        await service.NotifyProcessExitAsync(
            CreateIdentity(),
            exitCode: 1,
            shutdownRequested: false,
            policyRequestedStop: false,
            new PluginRuntimeProcessExitSummary(ExitedBeforeReady: true, []),
            CancellationToken.None);

        telemetry.RecordedAgentExitedBeforeReady.ShouldBe(1);
        var healthEvent = await ReadHealthEventAsync(healthEvents);
        healthEvent.EventType.ShouldBe(PluginAgentHealthEventType.AgentExitedBeforeReady);
    }

    private static PluginAgentExitNotificationService CreateService(
        out PluginRuntimeAgentTelemetry telemetry,
        out PluginAgentHealthEventBus healthEvents)
    {
        telemetry = new PluginRuntimeAgentTelemetry();
        healthEvents = new PluginAgentHealthEventBus();
        return new PluginAgentExitNotificationService(
            healthEvents,
            telemetry,
            NullLogger<PluginAgentExitNotificationService>.Instance);
    }

    private static async Task<PluginAgentHealthEvent> ReadHealthEventAsync(
        PluginAgentHealthEventBus healthEvents)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = healthEvents.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        (await reader.MoveNextAsync()).ShouldBeTrue();
        return reader.Current;
    }

    private static async Task<bool> HasHealthEventAsync(
        PluginAgentHealthEventBus healthEvents)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await using var reader = healthEvents.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        try
        {
            return await reader.MoveNextAsync();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static PluginAgentIdentity CreateIdentity() =>
        new(
            Guid.NewGuid(),
            TaskVersionId: 101,
            TaskDefinitionId: 10,
            "TestPlugin",
            new Version(1, 0, 0));
}
