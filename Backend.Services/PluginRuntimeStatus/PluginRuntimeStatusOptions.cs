namespace Backend.Services.PluginRuntimeStatus;

public sealed class PluginRuntimeStatusOptions
{
    public int HeartbeatIntervalSeconds { get; init; } = 30;

    public int StaleAfterSeconds { get; init; } = 120;

    public TimeSpan HeartbeatInterval =>
        TimeSpan.FromSeconds(Math.Clamp(HeartbeatIntervalSeconds, 1, 3600));

    public TimeSpan StaleAfter =>
        TimeSpan.FromSeconds(Math.Clamp(StaleAfterSeconds, 1, 86400));
}
