namespace Backend.Maintenance;

public sealed class StepFinalizationRecoveryOptions
{
    public bool Enabled { get; init; }

    public int PollIntervalSeconds { get; init; } = 30;

    public int MinRecordAgeSeconds { get; init; } = 300;

    public int BatchSize { get; init; } = 50;

    public int MaxRetryCount { get; init; } = 10;

    public TimeSpan EffectivePollInterval =>
        TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));

    public TimeSpan EffectiveMinRecordAge =>
        TimeSpan.FromSeconds(Math.Max(1, MinRecordAgeSeconds));

    public int EffectiveBatchSize =>
        Math.Clamp(BatchSize, 1, 500);

    public int EffectiveMaxRetryCount =>
        Math.Max(1, MaxRetryCount);
}
