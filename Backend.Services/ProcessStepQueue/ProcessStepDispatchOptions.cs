namespace Backend.Services.ProcessStepQueue;

public sealed class ProcessStepDispatchOptions
{
    public bool Enabled { get; init; } = true;

    public int BatchSize { get; init; } = 100;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromMinutes(1);
}
