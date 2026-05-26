using System;
using System.Collections.Generic;
using System.Linq;
using Backend.Engine;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginAgentHealthPolicyTests
{
    [Test]
    public void Protocol_malformed_threshold_quarantines_agent_and_schedules_restart()
    {
        var telemetry = new PluginAgentHealthTelemetry();
        var policy = CreatePolicy(
            telemetry,
            new PluginAgentHealthPolicyOptions
            {
                MaxMalformedFramesBeforeQuarantine = 2,
                RestartBackoffSeconds = 5
            });
        var identity = CreateIdentity();

        var first = policy.Evaluate(CreateEvent(identity, PluginAgentHealthEventType.ProtocolMalformed));
        var second = policy.Evaluate(CreateEvent(identity, PluginAgentHealthEventType.ProtocolMalformed));

        first.HasOutcome(PluginAgentHealthPolicyOutcome.Warn).ShouldBeTrue();
        first.HasOutcome(PluginAgentHealthPolicyOutcome.QuarantineAgent).ShouldBeFalse();
        second.HasOutcome(PluginAgentHealthPolicyOutcome.QuarantineAgent).ShouldBeTrue();
        second.HasOutcome(PluginAgentHealthPolicyOutcome.RestartAgent).ShouldBeTrue();
        second.BackoffUntil.ShouldNotBeNull();
        policy.IsAgentQuarantined(identity.AgentInstanceId).ShouldBeTrue();
        telemetry.RecordedQuarantinedAgents.ShouldBe(1);
        telemetry.RecordedAgentRestarts.ShouldBe(1);
    }

    [Test]
    public void Crash_window_threshold_quarantines_task_version_runtime_only()
    {
        var telemetry = new PluginAgentHealthTelemetry();
        var policy = CreatePolicy(
            telemetry,
            new PluginAgentHealthPolicyOptions
            {
                MaxCrashesPerWindow = 3,
                CrashWindowSeconds = 60
            });
        var identity = CreateIdentity();
        var now = DateTimeOffset.UtcNow;

        var decisions = Enumerable.Range(0, 3)
            .Select(offset => policy.Evaluate(CreateEvent(
                identity,
                PluginAgentHealthEventType.RuntimeLoadFailed,
                occurredAt: now.AddSeconds(offset))))
            .ToArray();

        decisions[0].HasOutcome(PluginAgentHealthPolicyOutcome.RestartAgent).ShouldBeTrue();
        decisions[2].HasOutcome(PluginAgentHealthPolicyOutcome.QuarantineTaskVersion).ShouldBeTrue();
        decisions[2].RepeatedCrashThresholdCrossed.ShouldBeTrue();
        policy.IsTaskVersionQuarantined(identity.TaskVersionId, out var reason).ShouldBeTrue();
        reason.ShouldNotBeNull().ShouldContain("Repeated crash threshold");
        telemetry.RecordedRepeatedCrashes.ShouldBe(1);
        telemetry.RecordedQuarantinedTaskVersions.ShouldBe(1);
    }

    [Test]
    public void Unknown_result_is_warn_only()
    {
        var policy = CreatePolicy();

        var decision = policy.Evaluate(
            CreateEvent(
                CreateIdentity(),
                PluginAgentHealthEventType.ResultForUnknownJob,
                [Guid.NewGuid()]));

        decision.Outcomes.ShouldBe(PluginAgentHealthPolicyOutcome.Warn);
    }

    [Test]
    public void Heartbeat_miss_restarts_agent_and_retries_active_steps()
    {
        var policy = CreatePolicy();

        var decision = policy.Evaluate(
            CreateEvent(
                CreateIdentity(),
                PluginAgentHealthEventType.HeartbeatMissed,
                [Guid.NewGuid()]));

        decision.HasOutcome(PluginAgentHealthPolicyOutcome.RestartAgent).ShouldBeTrue();
        decision.HasOutcome(PluginAgentHealthPolicyOutcome.RetryActiveStep).ShouldBeTrue();
        decision.BackoffUntil.ShouldNotBeNull();
    }

    [Test]
    public void Policy_requested_stop_retries_inflight_steps_without_counting_crash()
    {
        var telemetry = new PluginAgentHealthTelemetry();
        var policy = CreatePolicy(
            telemetry,
            new PluginAgentHealthPolicyOptions
            {
                MaxCrashesPerWindow = 1
            });
        var identity = CreateIdentity();

        var decision = policy.Evaluate(
            CreateEvent(
                identity,
                PluginAgentHealthEventType.AgentExitedWithInFlightJobs,
                [Guid.NewGuid()],
                policyRequestedStop: true));

        decision.HasOutcome(PluginAgentHealthPolicyOutcome.RetryActiveStep).ShouldBeTrue();
        decision.HasOutcome(PluginAgentHealthPolicyOutcome.QuarantineTaskVersion).ShouldBeFalse();
        policy.IsTaskVersionQuarantined(identity.TaskVersionId, out _).ShouldBeFalse();
        telemetry.RecordedRepeatedCrashes.ShouldBe(0);
    }

    private static PluginAgentHealthPolicy CreatePolicy(
        PluginAgentHealthTelemetry? telemetry = null,
        PluginAgentHealthPolicyOptions? options = null) =>
        new(
            options ?? new PluginAgentHealthPolicyOptions(),
            telemetry ?? new PluginAgentHealthTelemetry());

    private static PluginAgentHealthEvent CreateEvent(
        PluginAgentIdentity identity,
        PluginAgentHealthEventType eventType,
        IReadOnlyCollection<Guid>? processStepIds = null,
        DateTimeOffset? occurredAt = null,
        bool policyRequestedStop = false) =>
        PluginAgentHealthEvent.Create(
            identity,
            eventType,
            processStepIds,
            message: eventType.ToString(),
            occurredAt: occurredAt,
            policyRequestedStop: policyRequestedStop);

    private static PluginAgentIdentity CreateIdentity() =>
        new(
            Guid.NewGuid(),
            TaskVersionId: 101,
            TaskDefinitionId: 10,
            "HealthPlugin",
            new Version(1, 0, 0));
}
