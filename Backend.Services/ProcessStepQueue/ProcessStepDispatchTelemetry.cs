using System.Diagnostics;
using System.Diagnostics.Metrics;
using Backend.Interfaces.Repositories;

namespace Backend.Services.ProcessStepQueue;

public sealed class ProcessStepDispatchTelemetry
{
    public const string MeterName = "WorkflowBuilder.ProcessStepDispatch";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> DispatchFailures = Meter.CreateCounter<long>("process_step_dispatch_failures_total");
    private static readonly Histogram<double> DispatchLatencyMs = Meter.CreateHistogram<double>("process_step_dispatch_latency_ms");
    private static long _pendingDispatchCount;
    private static double _oldestPendingDispatchAgeMs;

    static ProcessStepDispatchTelemetry()
    {
        Meter.CreateObservableGauge(
            "process_step_dispatch_pending",
            () => Volatile.Read(ref _pendingDispatchCount));
        Meter.CreateObservableGauge(
            "process_step_dispatch_oldest_pending_age_ms",
            () => Volatile.Read(ref _oldestPendingDispatchAgeMs));
    }

    public void RecordBacklog(
        ProcessStepDispatchBacklogSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        Interlocked.Exchange(ref _pendingDispatchCount, snapshot.PendingCount);
        var oldestAgeMs = snapshot.OldestPendingCreatedAt is DateTimeOffset oldest
            ? Math.Max(0, (observedAt - oldest).TotalMilliseconds)
            : 0;
        Volatile.Write(ref _oldestPendingDispatchAgeMs, oldestAgeMs);
    }

    public void RecordFailure(string errorKind)
    {
        var tags = new TagList
        {
            { "dispatch.error_kind", errorKind }
        };
        DispatchFailures.Add(1, tags);
    }

    public void RecordLatency(DateTimeOffset createdAt, DateTimeOffset dispatchedAt)
    {
        DispatchLatencyMs.Record(
            Math.Max(0, (dispatchedAt - createdAt).TotalMilliseconds));
    }
}
