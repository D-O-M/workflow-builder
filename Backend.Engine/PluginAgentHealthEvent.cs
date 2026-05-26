namespace Backend.Engine;

public enum PluginAgentHealthEventType
{
    AgentExitedBeforeReady,
    AgentExitedWithInFlightJobs,
    ProtocolMalformed,
    ResultForUnknownJob,
    FinalizationQueueClosed,
    HeartbeatMissed,
    StartupTimeout,
    CapabilityValidationFailed,
    RuntimeLoadFailed,
    RepeatedCrash
}

[Flags]
public enum PluginAgentHealthPolicyOutcome
{
    Ignore = 0,
    Warn = 1,
    RestartAgent = 2,
    QuarantineAgent = 4,
    QuarantineTaskVersion = 8,
    FailActiveStep = 16,
    RetryActiveStep = 32
}

public sealed record PluginAgentHealthEvent(
    PluginAgentIdentity Identity,
    PluginAgentHealthEventType EventType,
    IReadOnlyCollection<Guid> ProcessStepIds,
    int? ExitCode,
    string? ExceptionType,
    string? Message,
    DateTimeOffset OccurredAt,
    bool PolicyRequestedStop)
{
    public static PluginAgentHealthEvent Create(
        PluginAgentIdentity identity,
        PluginAgentHealthEventType eventType,
        IReadOnlyCollection<Guid>? processStepIds = null,
        int? exitCode = null,
        Exception? exception = null,
        string? message = null,
        DateTimeOffset? occurredAt = null,
        bool policyRequestedStop = false) =>
        new(
            identity,
            eventType,
            processStepIds ?? [],
            exitCode,
            exception?.GetType().FullName ?? exception?.GetType().Name,
            message ?? exception?.Message,
            occurredAt ?? DateTimeOffset.UtcNow,
            policyRequestedStop);
}

public sealed record PluginAgentHealthPolicyDecision(
    PluginAgentHealthEvent Event,
    PluginAgentHealthPolicyOutcome Outcomes,
    string Reason,
    DateTimeOffset? BackoffUntil,
    bool RepeatedCrashThresholdCrossed)
{
    public bool HasOutcome(PluginAgentHealthPolicyOutcome outcome) =>
        (Outcomes & outcome) == outcome;
}

public interface IPluginAgentHealthDecisionHandler
{
    Task ApplyPluginAgentHealthDecisionAsync(
        PluginAgentHealthPolicyDecision decision,
        CancellationToken cancellationToken);
}
