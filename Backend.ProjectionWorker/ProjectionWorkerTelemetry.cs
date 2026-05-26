using System.Diagnostics.Metrics;

namespace Backend.ProjectionWorker;

public sealed class ProjectionWorkerTelemetry
{
    public const string MeterName = "WorkflowBuilder.ProjectionWorker";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> ProcessedItems =
        Meter.CreateCounter<long>("projection_worker_processed_items_total");
    private static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("projection_worker_failures_total");
    private static readonly Histogram<double> LagMs =
        Meter.CreateHistogram<double>("projection_worker_lag_ms");

    public void RecordProcessed(string source, long count)
    {
        if (count <= 0)
        {
            return;
        }

        ProcessedItems.Add(
            count,
            new KeyValuePair<string, object?>("projection.source", source));
    }

    public void RecordFailure(string source, string reason)
    {
        Failures.Add(
            1,
            new KeyValuePair<string, object?>("projection.source", source),
            new KeyValuePair<string, object?>("failure.reason", reason));
    }

    public void RecordFailure(string source, Exception exception) =>
        RecordFailure(source, exception.GetType().Name);

    public void RecordLag(
        string source,
        DateTimeOffset? changedAt,
        DateTimeOffset observedAt)
    {
        if (changedAt is null)
        {
            return;
        }

        LagMs.Record(
            Math.Max(0, (observedAt - changedAt.Value).TotalMilliseconds),
            new KeyValuePair<string, object?>("projection.source", source));
    }
}
