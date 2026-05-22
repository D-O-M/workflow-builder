namespace Backend.Engine;

public sealed class ProcessStartReconciliationOptions
{
    public bool Enabled { get; init; }

    public int PollIntervalSeconds { get; init; } = 30;

    public int BatchSize { get; init; } = 50;

    public int MaxProcessAgeToScanHours { get; init; } = 24;

    public TimeSpan EffectivePollInterval =>
        TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));

    public int EffectiveBatchSize =>
        Math.Clamp(BatchSize, 1, 500);

    public TimeSpan EffectiveMaxProcessAgeToScan =>
        TimeSpan.FromHours(Math.Max(1, MaxProcessAgeToScanHours));
}
