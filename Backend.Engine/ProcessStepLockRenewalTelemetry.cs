using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Backend.Engine;

public sealed class ProcessStepLockRenewalTelemetry
{
    public const string MeterName = "WorkflowBuilder.Engine.ProcessStepLocks";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> LockRenewals = Meter.CreateCounter<long>("process_step_message_lock_renewals_total");
    private static readonly Counter<long> LockRenewalFailures = Meter.CreateCounter<long>("process_step_message_lock_renewal_failures_total");
    private static readonly Counter<long> LockLostSuspected = Meter.CreateCounter<long>("process_step_message_lock_lost_suspected_total");
    private static readonly Histogram<double> ActiveRenewalDurationMs = Meter.CreateHistogram<double>("process_step_message_lock_active_renewal_duration_ms");

    private long _recordedRenewals;
    private long _recordedRenewalFailures;
    private long _recordedLockLostSuspected;
    private long _recordedActiveRenewalDurations;

    public long RecordedRenewals => Interlocked.Read(ref _recordedRenewals);

    public long RecordedRenewalFailures => Interlocked.Read(ref _recordedRenewalFailures);

    public long RecordedLockLostSuspected => Interlocked.Read(ref _recordedLockLostSuspected);

    public long RecordedActiveRenewalDurations => Interlocked.Read(ref _recordedActiveRenewalDurations);

    public void RecordRenewal()
    {
        Interlocked.Increment(ref _recordedRenewals);
        LockRenewals.Add(1);
    }

    public void RecordRenewalFailure(Exception exception)
    {
        Interlocked.Increment(ref _recordedRenewalFailures);
        var tags = new TagList
        {
            { "exception.type", exception.GetType().FullName ?? exception.GetType().Name }
        };
        LockRenewalFailures.Add(1, tags);
    }

    public void RecordLockLostSuspected(string reason)
    {
        Interlocked.Increment(ref _recordedLockLostSuspected);
        var tags = new TagList
        {
            { "reason", reason }
        };
        LockLostSuspected.Add(1, tags);
    }

    public void RecordActiveRenewalDuration(TimeSpan elapsed, string outcome)
    {
        Interlocked.Increment(ref _recordedActiveRenewalDurations);
        var tags = new TagList
        {
            { "outcome", outcome }
        };
        ActiveRenewalDurationMs.Record(Math.Max(0, elapsed.TotalMilliseconds), tags);
    }
}
