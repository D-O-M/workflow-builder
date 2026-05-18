using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Backend.Engine;

public sealed class PluginRuntimeAgentTelemetry
{
    public const string MeterName = "WorkflowBuilder.PluginRuntimeAgent";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> ProtocolErrors = Meter.CreateCounter<long>("plugin_runtime_protocol_errors_total");
    private static readonly Counter<long> UnknownJobResults = Meter.CreateCounter<long>("plugin_runtime_unknown_job_results_total");
    private static readonly Counter<long> InFlightJobsOnExit = Meter.CreateCounter<long>("plugin_runtime_inflight_jobs_on_exit_total");
    private static readonly Counter<long> Heartbeats = Meter.CreateCounter<long>("plugin_runtime_heartbeats_total");

    private long _recordedProtocolErrors;
    private long _recordedUnknownJobResults;
    private long _recordedInFlightJobsOnExit;
    private long _recordedHeartbeats;

    public long RecordedProtocolErrors => Interlocked.Read(ref _recordedProtocolErrors);

    public long RecordedUnknownJobResults => Interlocked.Read(ref _recordedUnknownJobResults);

    public long RecordedInFlightJobsOnExit => Interlocked.Read(ref _recordedInFlightJobsOnExit);

    public long RecordedHeartbeats => Interlocked.Read(ref _recordedHeartbeats);

    public void RecordProtocolError(string pluginName, Exception exception)
    {
        Interlocked.Increment(ref _recordedProtocolErrors);
        var tags = new TagList
        {
            { "plugin.name", pluginName },
            { "exception.type", exception.GetType().FullName ?? exception.GetType().Name }
        };
        ProtocolErrors.Add(1, tags);
    }

    public void RecordUnknownJobResult(string pluginName)
    {
        Interlocked.Increment(ref _recordedUnknownJobResults);
        var tags = new TagList
        {
            { "plugin.name", pluginName }
        };
        UnknownJobResults.Add(1, tags);
    }

    public void RecordInFlightJobsOnExit(string pluginName, int jobCount)
    {
        if (jobCount <= 0)
        {
            return;
        }

        Interlocked.Add(ref _recordedInFlightJobsOnExit, jobCount);
        var tags = new TagList
        {
            { "plugin.name", pluginName }
        };
        InFlightJobsOnExit.Add(jobCount, tags);
    }

    public void RecordHeartbeat(string pluginName)
    {
        Interlocked.Increment(ref _recordedHeartbeats);
        var tags = new TagList
        {
            { "plugin.name", pluginName }
        };
        Heartbeats.Add(1, tags);
    }
}
