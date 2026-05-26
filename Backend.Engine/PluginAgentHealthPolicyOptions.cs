namespace Backend.Engine;

public sealed class PluginAgentHealthPolicyOptions
{
    public const string SectionName = "PluginAgentHealth";

    public int MaxMalformedFramesBeforeQuarantine { get; init; } = 5;

    public int MaxCrashesPerWindow { get; init; } = 3;

    public int CrashWindowSeconds { get; init; } = 300;

    public int HeartbeatTimeoutSeconds { get; init; } = 30;

    public int RestartBackoffSeconds { get; init; } = 5;

    public int EffectiveMaxMalformedFramesBeforeQuarantine =>
        Math.Max(1, MaxMalformedFramesBeforeQuarantine);

    public int EffectiveMaxCrashesPerWindow =>
        Math.Max(1, MaxCrashesPerWindow);

    public TimeSpan EffectiveCrashWindow =>
        TimeSpan.FromSeconds(Math.Max(1, CrashWindowSeconds));

    public TimeSpan EffectiveHeartbeatTimeout =>
        TimeSpan.FromSeconds(Math.Max(1, HeartbeatTimeoutSeconds));

    public TimeSpan EffectiveRestartBackoff =>
        TimeSpan.FromSeconds(Math.Max(0, RestartBackoffSeconds));
}
