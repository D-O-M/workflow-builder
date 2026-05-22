using System.Diagnostics.Metrics;

namespace Backend.Repositories.Processes;

public sealed class ProcessRealtimeProjectionTelemetry
{
    public const string MeterName = "WorkflowBuilder.RealtimeProjections";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<double> ProjectionReadLatencyMs =
        Meter.CreateHistogram<double>("process_realtime_projection_read_latency_ms");
    private static readonly Counter<long> ProjectionFallbacks =
        Meter.CreateCounter<long>("process_realtime_projection_fallback_total");
    private static readonly Counter<long> ProjectionMissing =
        Meter.CreateCounter<long>("process_realtime_projection_missing_total");

    public void RecordReadLatency(TimeSpan elapsed, string projectionType)
    {
        ProjectionReadLatencyMs.Record(
            Math.Max(0, elapsed.TotalMilliseconds),
            new KeyValuePair<string, object?>("projection.type", projectionType));
    }

    public void RecordFallback(string reason)
    {
        ProjectionFallbacks.Add(
            1,
            new KeyValuePair<string, object?>("fallback.reason", reason));
    }

    public void RecordMissing(string projectionType)
    {
        ProjectionMissing.Add(
            1,
            new KeyValuePair<string, object?>("projection.type", projectionType));
    }
}
