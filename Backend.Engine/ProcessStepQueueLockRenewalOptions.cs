namespace Backend.Engine;

public sealed class ProcessStepQueueLockRenewalOptions
{
    public const string SectionName = "ProcessStepQueue";

    public bool ActiveMessageLockRenewalEnabled { get; set; } = true;

    public int RenewalLeadTimeSeconds { get; set; } = 20;

    public int MaxActiveRenewalDurationMinutes { get; set; } = 30;

    public TimeSpan EffectiveRenewalLeadTime =>
        TimeSpan.FromSeconds(Math.Clamp(RenewalLeadTimeSeconds, 1, 300));

    public TimeSpan EffectiveMaxActiveRenewalDuration =>
        TimeSpan.FromMinutes(Math.Max(1, MaxActiveRenewalDurationMinutes));
}
