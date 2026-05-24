using System.Diagnostics;
using System.Diagnostics.Metrics;
using Backend.Interfaces.Repositories;

namespace Backend.Maintenance;

public sealed class StepFinalizationRecoveryTelemetry
{
    public const string MeterName = "WorkflowBuilder.StepFinalizationRecovery";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> IncompleteFound =
        Meter.CreateCounter<long>("process_step_finalization_recovery_incomplete_found_total");
    private static readonly Counter<long> Recovered =
        Meter.CreateCounter<long>("process_step_finalization_recovered_total");
    private static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("process_step_finalization_recovery_failures_total");

    private static long _incompleteCount;
    private static double _oldestIncompleteAgeMs;

    static StepFinalizationRecoveryTelemetry()
    {
        Meter.CreateObservableGauge(
            "process_step_finalization_recovery_oldest_incomplete_age_ms",
            () => Volatile.Read(ref _oldestIncompleteAgeMs));
    }

    public long IncompleteCount => Interlocked.Read(ref _incompleteCount);

    public void RecordBacklog(
        ProcessStepFinalizationRecoveryBacklogSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        Interlocked.Exchange(ref _incompleteCount, snapshot.IncompleteCount);
        var oldestAgeMs = snapshot.OldestIncompleteUpdatedAt is DateTimeOffset oldest
            ? Math.Max(0, (observedAt - oldest).TotalMilliseconds)
            : 0;
        Volatile.Write(ref _oldestIncompleteAgeMs, oldestAgeMs);
    }

    public void RecordFound(long count)
    {
        if (count <= 0)
        {
            return;
        }

        IncompleteFound.Add(count);
    }

    public void RecordRecovered(string outcome, string source)
    {
        var tags = new TagList
        {
            { "finalization.outcome", outcome },
            { "finalization.source", source }
        };
        Recovered.Add(1, tags);
    }

    public void RecordFailure(string reason)
    {
        var tags = new TagList
        {
            { "failure.reason", reason }
        };
        Failures.Add(1, tags);
    }

    public void RecordFailure(Exception exception)
    {
        var tags = new TagList
        {
            { "exception.type", exception.GetType().FullName ?? exception.GetType().Name }
        };
        Failures.Add(1, tags);
    }
}
