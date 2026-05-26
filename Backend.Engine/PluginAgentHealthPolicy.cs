namespace Backend.Engine;

public sealed class PluginAgentHealthPolicy
{
    private readonly PluginAgentHealthPolicyOptions _options;
    private readonly PluginAgentHealthTelemetry _telemetry;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, int> _malformedFramesByAgent = [];
    private readonly Dictionary<int, List<DateTimeOffset>> _crashesByTaskVersion = [];
    private readonly Dictionary<int, PluginTaskVersionQuarantineState> _quarantinedTaskVersions = [];
    private readonly HashSet<Guid> _quarantinedAgents = [];

    public PluginAgentHealthPolicy(
        PluginAgentHealthPolicyOptions options,
        PluginAgentHealthTelemetry telemetry)
    {
        _options = options;
        _telemetry = telemetry;
    }

    public PluginAgentHealthPolicyDecision Evaluate(PluginAgentHealthEvent healthEvent)
    {
        ArgumentNullException.ThrowIfNull(healthEvent);

        lock (_sync)
        {
            _telemetry.RecordHealthEvent(healthEvent);

            var decision = healthEvent.EventType switch
            {
                PluginAgentHealthEventType.ProtocolMalformed => EvaluateMalformedFrame(healthEvent),
                PluginAgentHealthEventType.ResultForUnknownJob => WarnOnly(
                    healthEvent,
                    "Plugin returned a result for an unknown or inactive process step."),
                PluginAgentHealthEventType.FinalizationQueueClosed => RetryActiveStep(
                    healthEvent,
                    "Plugin result finalization queue was closed before the result could be persisted."),
                PluginAgentHealthEventType.HeartbeatMissed => EvaluateCrashLikeEvent(
                    healthEvent,
                    PluginAgentHealthPolicyOutcome.Warn |
                    PluginAgentHealthPolicyOutcome.RestartAgent |
                    PluginAgentHealthPolicyOutcome.RetryActiveStep,
                    "Plugin agent heartbeat was missed."),
                PluginAgentHealthEventType.AgentExitedWithInFlightJobs => EvaluateAgentExitWithInFlightJobs(healthEvent),
                PluginAgentHealthEventType.AgentExitedBeforeReady => EvaluateCrashLikeEvent(
                    healthEvent,
                    PluginAgentHealthPolicyOutcome.Warn | PluginAgentHealthPolicyOutcome.RestartAgent,
                    "Plugin agent exited before signalling readiness."),
                PluginAgentHealthEventType.StartupTimeout => EvaluateCrashLikeEvent(
                    healthEvent,
                    PluginAgentHealthPolicyOutcome.Warn | PluginAgentHealthPolicyOutcome.RestartAgent,
                    "Plugin agent startup timed out."),
                PluginAgentHealthEventType.RuntimeLoadFailed => EvaluateCrashLikeEvent(
                    healthEvent,
                    PluginAgentHealthPolicyOutcome.Warn | PluginAgentHealthPolicyOutcome.RestartAgent,
                    "Plugin runtime load failed."),
                PluginAgentHealthEventType.CapabilityValidationFailed => WarnOnly(
                    healthEvent,
                    "Plugin capability validation failed."),
                PluginAgentHealthEventType.RepeatedCrash => QuarantineTaskVersion(
                    healthEvent,
                    "Plugin task version crossed the repeated-crash threshold."),
                _ => new PluginAgentHealthPolicyDecision(
                    healthEvent,
                    PluginAgentHealthPolicyOutcome.Ignore,
                    "No plugin-agent health policy matched the event.",
                    BackoffUntil: null,
                    RepeatedCrashThresholdCrossed: false)
            };

            RecordOutcomeMetrics(decision);
            return decision;
        }
    }

    public bool IsTaskVersionQuarantined(int taskVersionId, out string? reason)
    {
        lock (_sync)
        {
            if (_quarantinedTaskVersions.TryGetValue(taskVersionId, out var state))
            {
                reason = state.Reason;
                return true;
            }
        }

        reason = null;
        return false;
    }

    public bool IsAgentQuarantined(Guid agentInstanceId)
    {
        lock (_sync)
        {
            return _quarantinedAgents.Contains(agentInstanceId);
        }
    }

    private PluginAgentHealthPolicyDecision EvaluateMalformedFrame(
        PluginAgentHealthEvent healthEvent)
    {
        var count = _malformedFramesByAgent.GetValueOrDefault(healthEvent.Identity.AgentInstanceId) + 1;
        _malformedFramesByAgent[healthEvent.Identity.AgentInstanceId] = count;

        if (count < _options.EffectiveMaxMalformedFramesBeforeQuarantine)
        {
            return WarnOnly(
                healthEvent,
                $"Plugin agent emitted malformed protocol frame {count}/{_options.EffectiveMaxMalformedFramesBeforeQuarantine}.");
        }

        _quarantinedAgents.Add(healthEvent.Identity.AgentInstanceId);
        return new PluginAgentHealthPolicyDecision(
            healthEvent,
            PluginAgentHealthPolicyOutcome.Warn |
            PluginAgentHealthPolicyOutcome.QuarantineAgent |
            PluginAgentHealthPolicyOutcome.RestartAgent,
            $"Plugin agent emitted {count} malformed protocol frames and will be restarted.",
            healthEvent.OccurredAt.Add(_options.EffectiveRestartBackoff),
            RepeatedCrashThresholdCrossed: false);
    }

    private PluginAgentHealthPolicyDecision EvaluateAgentExitWithInFlightJobs(
        PluginAgentHealthEvent healthEvent)
    {
        var baseOutcome =
            PluginAgentHealthPolicyOutcome.Warn |
            PluginAgentHealthPolicyOutcome.RetryActiveStep;

        if (healthEvent.PolicyRequestedStop)
        {
            return new PluginAgentHealthPolicyDecision(
                healthEvent,
                baseOutcome,
                "Policy-requested plugin agent stop left in-flight process steps to retry.",
                BackoffUntil: null,
                RepeatedCrashThresholdCrossed: false);
        }

        return EvaluateCrashLikeEvent(
            healthEvent,
            baseOutcome | PluginAgentHealthPolicyOutcome.RestartAgent,
            "Plugin agent exited with in-flight process steps.");
    }

    private PluginAgentHealthPolicyDecision EvaluateCrashLikeEvent(
        PluginAgentHealthEvent healthEvent,
        PluginAgentHealthPolicyOutcome defaultOutcomes,
        string reason)
    {
        var repeatedCrashThresholdCrossed = !healthEvent.PolicyRequestedStop &&
            RecordCrashAndCheckThreshold(healthEvent);
        if (repeatedCrashThresholdCrossed)
        {
            return QuarantineTaskVersion(
                healthEvent,
                $"{reason} Repeated crash threshold was crossed.");
        }

        var backoffUntil = defaultOutcomes.HasFlag(PluginAgentHealthPolicyOutcome.RestartAgent)
            ? healthEvent.OccurredAt.Add(_options.EffectiveRestartBackoff)
            : (DateTimeOffset?)null;

        return new PluginAgentHealthPolicyDecision(
            healthEvent,
            defaultOutcomes,
            reason,
            backoffUntil,
            RepeatedCrashThresholdCrossed: false);
    }

    private bool RecordCrashAndCheckThreshold(PluginAgentHealthEvent healthEvent)
    {
        var taskVersionId = healthEvent.Identity.TaskVersionId;
        if (taskVersionId <= 0)
        {
            return false;
        }

        var windowStart = healthEvent.OccurredAt.Subtract(_options.EffectiveCrashWindow);
        if (!_crashesByTaskVersion.TryGetValue(taskVersionId, out var crashes))
        {
            crashes = [];
            _crashesByTaskVersion[taskVersionId] = crashes;
        }

        crashes.RemoveAll(timestamp => timestamp < windowStart);
        crashes.Add(healthEvent.OccurredAt);
        return crashes.Count >= _options.EffectiveMaxCrashesPerWindow;
    }

    private PluginAgentHealthPolicyDecision RetryActiveStep(
        PluginAgentHealthEvent healthEvent,
        string reason) =>
        new(
            healthEvent,
            PluginAgentHealthPolicyOutcome.Warn | PluginAgentHealthPolicyOutcome.RetryActiveStep,
            reason,
            BackoffUntil: null,
            RepeatedCrashThresholdCrossed: false);

    private static PluginAgentHealthPolicyDecision WarnOnly(
        PluginAgentHealthEvent healthEvent,
        string reason) =>
        new(
            healthEvent,
            PluginAgentHealthPolicyOutcome.Warn,
            reason,
            BackoffUntil: null,
            RepeatedCrashThresholdCrossed: false);

    private PluginAgentHealthPolicyDecision QuarantineTaskVersion(
        PluginAgentHealthEvent healthEvent,
        string reason)
    {
        _quarantinedAgents.Add(healthEvent.Identity.AgentInstanceId);
        if (healthEvent.Identity.TaskVersionId > 0)
        {
            _quarantinedTaskVersions[healthEvent.Identity.TaskVersionId] =
                new PluginTaskVersionQuarantineState(reason, healthEvent.OccurredAt);
        }

        return new PluginAgentHealthPolicyDecision(
            healthEvent,
            PluginAgentHealthPolicyOutcome.Warn |
            PluginAgentHealthPolicyOutcome.QuarantineAgent |
            PluginAgentHealthPolicyOutcome.QuarantineTaskVersion,
            reason,
            BackoffUntil: null,
            RepeatedCrashThresholdCrossed: true);
    }

    private void RecordOutcomeMetrics(PluginAgentHealthPolicyDecision decision)
    {
        if (decision.RepeatedCrashThresholdCrossed)
        {
            _telemetry.RecordRepeatedCrash(decision.Event);
        }

        if (decision.HasOutcome(PluginAgentHealthPolicyOutcome.RestartAgent))
        {
            _telemetry.RecordAgentRestart(decision.Event);
        }

        if (decision.HasOutcome(PluginAgentHealthPolicyOutcome.QuarantineAgent))
        {
            _telemetry.RecordAgentQuarantine(decision.Event);
        }

        if (decision.HasOutcome(PluginAgentHealthPolicyOutcome.QuarantineTaskVersion))
        {
            _telemetry.RecordTaskVersionQuarantine(decision.Event);
        }
    }

    private sealed record PluginTaskVersionQuarantineState(
        string Reason,
        DateTimeOffset QuarantinedAt);
}
