using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Backend.Engine;

public sealed class PluginAgentHealthTelemetry
{
    public const string MeterName = "WorkflowBuilder.PluginAgentHealth";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> HealthEvents = Meter.CreateCounter<long>("plugin_agent_health_events_total");
    private static readonly Counter<long> AgentRestarts = Meter.CreateCounter<long>("plugin_agent_restarts_total");
    private static readonly Counter<long> QuarantinedAgents = Meter.CreateCounter<long>("plugin_agent_quarantined_agents_total");
    private static readonly Counter<long> QuarantinedTaskVersions = Meter.CreateCounter<long>("plugin_agent_quarantined_task_versions_total");
    private static readonly Counter<long> RepeatedCrashes = Meter.CreateCounter<long>("plugin_agent_repeated_crashes_total");

    private long _recordedHealthEvents;
    private long _recordedAgentRestarts;
    private long _recordedQuarantinedAgents;
    private long _recordedQuarantinedTaskVersions;
    private long _recordedRepeatedCrashes;

    public long RecordedHealthEvents => Interlocked.Read(ref _recordedHealthEvents);

    public long RecordedAgentRestarts => Interlocked.Read(ref _recordedAgentRestarts);

    public long RecordedQuarantinedAgents => Interlocked.Read(ref _recordedQuarantinedAgents);

    public long RecordedQuarantinedTaskVersions => Interlocked.Read(ref _recordedQuarantinedTaskVersions);

    public long RecordedRepeatedCrashes => Interlocked.Read(ref _recordedRepeatedCrashes);

    public void RecordHealthEvent(PluginAgentHealthEvent healthEvent)
    {
        Interlocked.Increment(ref _recordedHealthEvents);
        HealthEvents.Add(1, CreateTags(healthEvent));
    }

    public void RecordAgentRestart(PluginAgentHealthEvent healthEvent)
    {
        Interlocked.Increment(ref _recordedAgentRestarts);
        AgentRestarts.Add(1, CreateTags(healthEvent));
    }

    public void RecordAgentQuarantine(PluginAgentHealthEvent healthEvent)
    {
        Interlocked.Increment(ref _recordedQuarantinedAgents);
        QuarantinedAgents.Add(1, CreateTags(healthEvent));
    }

    public void RecordTaskVersionQuarantine(PluginAgentHealthEvent healthEvent)
    {
        Interlocked.Increment(ref _recordedQuarantinedTaskVersions);
        QuarantinedTaskVersions.Add(1, CreateTags(healthEvent));
    }

    public void RecordRepeatedCrash(PluginAgentHealthEvent healthEvent)
    {
        Interlocked.Increment(ref _recordedRepeatedCrashes);
        RepeatedCrashes.Add(1, CreateTags(healthEvent));
    }

    private static TagList CreateTags(PluginAgentHealthEvent healthEvent) =>
        new()
        {
            { "plugin.name", healthEvent.Identity.PluginName },
            { "plugin.version", healthEvent.Identity.PluginVersion.ToString() },
            { "task.version.id", healthEvent.Identity.TaskVersionId },
            { "event.type", healthEvent.EventType.ToString() }
        };
}
