using System.Diagnostics;
using Backend.Plugins.Communication;

namespace Backend.Plugins.Harness;

internal static class PluginInvocationTelemetry
{
    public static Activity? StartExecutionActivity(InvocationEnvelope envelope, string pluginName)
    {
        var activity = TryGetParentContext(envelope, out var parentContext)
            ? PluginExecutionTelemetry.ActivitySource.StartActivity(
                "plugin.execute",
                ActivityKind.Internal,
                parentContext)
            : PluginExecutionTelemetry.ActivitySource.StartActivity(
                "plugin.execute",
                ActivityKind.Internal);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("process.id", envelope.ProcessId.ToString());
        activity.SetTag("process.step_id", envelope.ProcessStepId.ToString());
        activity.SetTag("workflow.instance_id", envelope.ProcessId.ToString());
        activity.SetTag("workflow.task_instance_id", envelope.ProcessStepId.ToString());
        activity.SetTag("blueprint.definition_id", envelope.BlueprintDefinitionId);
        activity.SetTag("blueprint.version_id", envelope.ExecutableBlueprintId);
        activity.SetTag("blueprint.node_id", envelope.BlueprintNodeId.ToString());
        activity.SetTag("task.definition_id", envelope.TaskDefinitionId);
        activity.SetTag("task.version_id", envelope.TaskVersionId);
        activity.SetTag("task.version", envelope.TaskVersion);
        activity.SetTag("plugin.name", pluginName);
        activity.SetTag("event.name", "plugin.execute");

        return activity;
    }

    public static Dictionary<string, object?> BuildExecutionScope(
        InvocationEnvelope envelope,
        string pluginName) =>
        new(StringComparer.Ordinal)
        {
            ["process.id"] = envelope.ProcessId,
            ["process.step_id"] = envelope.ProcessStepId,
            ["workflow.instance_id"] = envelope.ProcessId,
            ["workflow.task_instance_id"] = envelope.ProcessStepId,
            ["blueprint.definition_id"] = envelope.BlueprintDefinitionId,
            ["blueprint.version_id"] = envelope.ExecutableBlueprintId,
            ["blueprint.node_id"] = envelope.BlueprintNodeId,
            ["task.definition_id"] = envelope.TaskDefinitionId,
            ["task.version_id"] = envelope.TaskVersionId,
            ["task.version"] = envelope.TaskVersion,
            ["plugin.name"] = pluginName,
            ["event.name"] = "plugin.execute"
        };

    private static bool TryGetParentContext(
        InvocationEnvelope envelope,
        out ActivityContext parentContext)
    {
        parentContext = default;
        var telemetryContext = envelope.TelemetryContext;
        return telemetryContext is not null &&
               ActivityContext.TryParse(
                   telemetryContext.TraceParent,
                   telemetryContext.TraceState,
                   out parentContext);
    }
}
