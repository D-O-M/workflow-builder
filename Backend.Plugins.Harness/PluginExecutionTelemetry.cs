using System.Diagnostics;

namespace Backend.Plugins.Harness;

internal static class PluginExecutionTelemetry
{
    public const string ActivitySourceName = "WorkflowBuilder.PluginExecution";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
}
