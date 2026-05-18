using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Backend.Engine;

public sealed class PluginResultFinalizationTelemetry
{
    public const string MeterName = "WorkflowBuilder.PluginResultFinalization";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<double> EnqueueWaitMs = Meter.CreateHistogram<double>("plugin_result_finalization_enqueue_wait_ms");
    private static readonly Counter<long> Finalizations = Meter.CreateCounter<long>("plugin_result_finalizations_total");
    private static readonly Counter<long> FinalizationFailures = Meter.CreateCounter<long>("plugin_result_finalization_failures_total");
    private static long _queueDepth;
    private static double _oldestQueuedResultAgeMs;

    private long _recordedEnqueueWaits;
    private long _recordedFinalizations;
    private long _recordedFailures;

    static PluginResultFinalizationTelemetry()
    {
        Meter.CreateObservableGauge(
            "plugin_result_finalization_queue_depth",
            () => Volatile.Read(ref _queueDepth));
        Meter.CreateObservableGauge(
            "plugin_result_finalization_oldest_result_age_ms",
            () => Volatile.Read(ref _oldestQueuedResultAgeMs));
    }

    public long RecordedEnqueueWaits => Interlocked.Read(ref _recordedEnqueueWaits);

    public long RecordedFinalizations => Interlocked.Read(ref _recordedFinalizations);

    public long RecordedFailures => Interlocked.Read(ref _recordedFailures);

    public void RecordQueueSnapshot(int depth, DateTimeOffset? oldestQueuedResultAt, DateTimeOffset observedAt)
    {
        Interlocked.Exchange(ref _queueDepth, depth);
        var oldestAgeMs = oldestQueuedResultAt is DateTimeOffset oldest
            ? Math.Max(0, (observedAt - oldest).TotalMilliseconds)
            : 0;
        Volatile.Write(ref _oldestQueuedResultAgeMs, oldestAgeMs);
    }

    public void RecordEnqueueWait(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _recordedEnqueueWaits);
        EnqueueWaitMs.Record(Math.Max(0, elapsed.TotalMilliseconds));
    }

    public void RecordFinalizationCompleted(bool succeeded)
    {
        Interlocked.Increment(ref _recordedFinalizations);
        var tags = new TagList
        {
            { "finalization.outcome", succeeded ? "succeeded" : "failed" }
        };
        Finalizations.Add(1, tags);
    }

    public void RecordFailure(Exception exception)
    {
        Interlocked.Increment(ref _recordedFailures);
        var tags = new TagList
        {
            { "exception.type", exception.GetType().FullName ?? exception.GetType().Name }
        };
        FinalizationFailures.Add(1, tags);
    }
}
