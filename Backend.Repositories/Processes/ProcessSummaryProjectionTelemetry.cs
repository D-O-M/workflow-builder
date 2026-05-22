using System.Diagnostics;
using System.Diagnostics.Metrics;
using Backend.Database.State.Objects;

namespace Backend.Repositories.Processes;

public static class ProcessSummaryProjectionTelemetry
{
    public const string MeterName = "WorkflowBuilder.ProcessSummaryProjections";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<long> PartitionsQueried =
        Meter.CreateHistogram<long>("process_summary_projection_partitions_queried");
    private static readonly Histogram<double> QueryLatencyMs =
        Meter.CreateHistogram<double>("process_summary_projection_query_latency_ms");
    private static readonly Histogram<long> ItemsReturned =
        Meter.CreateHistogram<long>("process_summary_projection_items_returned");

    public static TimedQuery StartQuery(ProcessSummaryProjectionScope scope) =>
        new(scope);

    public readonly struct TimedQuery : IDisposable
    {
        private readonly Stopwatch _stopwatch;
        private readonly ProcessSummaryProjectionScope _scope;
        private readonly bool _active;

        internal TimedQuery(ProcessSummaryProjectionScope scope)
        {
            _scope = scope;
            _stopwatch = Stopwatch.StartNew();
            _active = true;
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            QueryLatencyMs.Record(
                Math.Max(0, _stopwatch.Elapsed.TotalMilliseconds),
                new KeyValuePair<string, object?>("process.summary.scope", _scope.ToString()));
        }
    }

    public static void RecordResult(
        ProcessSummaryProjectionScope scope,
        int partitionsQueried,
        int itemCount)
    {
        var tags = new TagList
        {
            { "process.summary.scope", scope.ToString() },
        };

        PartitionsQueried.Record(Math.Max(0, partitionsQueried), tags);
        ItemsReturned.Record(Math.Max(0, itemCount), tags);
    }
}
