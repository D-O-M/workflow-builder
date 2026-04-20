using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Backend.Models.Observability;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed class ProcessLifecycleTelemetry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("WorkflowBuilder.Engine.Processes", "1.0.0");
    private static readonly Counter<long> ProcessStepsStarted = Meter.CreateCounter<long>("process_step_started_total");
    private static readonly Counter<long> ProcessStepsCompleted = Meter.CreateCounter<long>("process_step_completed_total");
    private static readonly Counter<long> ProcessStepsFailed = Meter.CreateCounter<long>("process_step_failed_total");
    private static readonly Histogram<double> ProcessStepDurationMs = Meter.CreateHistogram<double>("process_step_duration_ms");

    private readonly ILogger<ProcessLifecycleTelemetry> _logger;

    public ProcessLifecycleTelemetry(ILogger<ProcessLifecycleTelemetry> logger)
    {
        _logger = logger;
    }

    public void LogProcessStarted(JobContext context, DateTimeOffset timestamp) =>
        WriteProcessLifecycle(
            context,
            new ProcessLifecycleLogEntry
            {
                EventCategory = "process.lifecycle",
                EventName = "process.started",
                Timestamp = timestamp,
                ProcessId = context.ProcessId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintName = context.BlueprintName,
                BlueprintVersionId = context.ExecutableBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                Status = "InProgress",
                ProcessOutcome = null,
                FailureMessage = null,
            });

    public void LogProcessCompleted(JobContext context, DateTimeOffset timestamp) =>
        WriteProcessLifecycle(
            context,
            new ProcessLifecycleLogEntry
            {
                EventCategory = "process.lifecycle",
                EventName = "process.completed",
                Timestamp = timestamp,
                ProcessId = context.ProcessId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintName = context.BlueprintName,
                BlueprintVersionId = context.ExecutableBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                Status = "Completed",
                ProcessOutcome = "Succeeded",
                FailureMessage = null,
            });

    public void LogProcessFailed(JobContext context, DateTimeOffset timestamp, string? failureMessage) =>
        WriteProcessLifecycle(
            context,
            new ProcessLifecycleLogEntry
            {
                EventCategory = "process.lifecycle",
                EventName = "process.failed",
                Timestamp = timestamp,
                ProcessId = context.ProcessId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintName = context.BlueprintName,
                BlueprintVersionId = context.ExecutableBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                Status = "Failed",
                ProcessOutcome = "Failed",
                FailureMessage = failureMessage,
            });

    public void LogStepStarted(JobContext context, DateTimeOffset timestamp)
    {
        ProcessStepsStarted.Add(1, BuildStepMetricTags(context, outcome: "started"));
        WriteStepLifecycle(
            context,
            new ProcessStepLifecycleLogEntry
            {
                EventCategory = "step.lifecycle",
                EventName = "step.started",
                Timestamp = timestamp,
                ProcessId = context.ProcessId,
                ProcessStepId = context.ProcessStepId,
                BlueprintNodeId = context.BlueprintNodeId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintName = context.BlueprintName,
                BlueprintVersionId = context.ExecutableBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                StepName = context.TaskName,
                PluginName = context.PluginName,
                TaskDefinitionId = context.TaskDefinitionId,
                TaskVersionId = context.ResolvedTaskVersionId,
                TaskVersion = context.BlueprintNode.ResolvedVersion.ToString(),
                Status = "InProgress",
                ProcessOutcome = null,
                DurationMs = null,
                FailureMessage = null,
            });
    }

    public void LogStepCompleted(JobContext context, PluginExecutionResult result)
    {
        var duration = (result.CompletedAt - result.StartedAt).TotalMilliseconds;
        var tags = BuildStepMetricTags(context, outcome: "succeeded");
        ProcessStepsCompleted.Add(1, tags);
        ProcessStepDurationMs.Record(duration, tags);

        WriteStepLifecycle(
            context,
            new ProcessStepLifecycleLogEntry
            {
                EventCategory = "step.lifecycle",
                EventName = "step.completed",
                Timestamp = result.CompletedAt,
                ProcessId = context.ProcessId,
                ProcessStepId = context.ProcessStepId,
                BlueprintNodeId = context.BlueprintNodeId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintName = context.BlueprintName,
                BlueprintVersionId = context.ExecutableBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                StepName = context.TaskName,
                PluginName = context.PluginName,
                TaskDefinitionId = context.TaskDefinitionId,
                TaskVersionId = context.ResolvedTaskVersionId,
                TaskVersion = context.BlueprintNode.ResolvedVersion.ToString(),
                Status = "Completed",
                ProcessOutcome = "Succeeded",
                DurationMs = duration,
                FailureMessage = null,
            });
    }

    public void LogStepFailed(JobContext context, PluginExecutionResult result)
    {
        var duration = (result.CompletedAt - result.StartedAt).TotalMilliseconds;
        var tags = BuildStepMetricTags(context, outcome: "failed");
        ProcessStepsFailed.Add(1, tags);
        ProcessStepDurationMs.Record(duration, tags);

        WriteStepLifecycle(
            context,
            new ProcessStepLifecycleLogEntry
            {
                EventCategory = "step.lifecycle",
                EventName = "step.failed",
                Timestamp = result.CompletedAt,
                ProcessId = context.ProcessId,
                ProcessStepId = context.ProcessStepId,
                BlueprintNodeId = context.BlueprintNodeId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintName = context.BlueprintName,
                BlueprintVersionId = context.ExecutableBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                StepName = context.TaskName,
                PluginName = context.PluginName,
                TaskDefinitionId = context.TaskDefinitionId,
                TaskVersionId = context.ResolvedTaskVersionId,
                TaskVersion = context.BlueprintNode.ResolvedVersion.ToString(),
                Status = "Failed",
                ProcessOutcome = "Failed",
                DurationMs = duration,
                FailureMessage = result.ErrorMessage,
            });
    }

    private void WriteProcessLifecycle(JobContext context, ProcessLifecycleLogEntry entry)
    {
        using var scope = _logger.BeginScope(BuildScope(context, entry.EventName, entry.ProcessOutcome));
        _logger.LogInformation("{LifecycleEvent}", JsonSerializer.Serialize(entry, JsonOptions));
    }

    private void WriteStepLifecycle(JobContext context, ProcessStepLifecycleLogEntry entry)
    {
        using var scope = _logger.BeginScope(BuildScope(context, entry.EventName, entry.ProcessOutcome));
        _logger.LogInformation("{LifecycleEvent}", JsonSerializer.Serialize(entry, JsonOptions));
    }

    private static Dictionary<string, object?> BuildScope(
        JobContext context,
        string eventName,
        string? processOutcome) =>
        new(StringComparer.Ordinal)
        {
            ["process.id"] = context.ProcessId,
            ["process.step_id"] = context.ProcessStepId,
            ["blueprint.definition_id"] = context.BlueprintDefinitionId,
            ["blueprint.version_id"] = context.ExecutableBlueprintId,
            ["blueprint.node_id"] = context.BlueprintNodeId,
            ["task.definition_id"] = context.TaskDefinitionId,
            ["task.version_id"] = context.ResolvedTaskVersionId,
            ["task.version"] = context.BlueprintNode.ResolvedVersion.ToString(),
            ["plugin.name"] = context.PluginName,
            ["event.name"] = eventName,
            ["process.outcome"] = processOutcome
        };

    private static TagList BuildStepMetricTags(JobContext context, string outcome)
    {
        var tags = new TagList
        {
            { "plugin.name", context.PluginName },
            { "task.definition_id", context.TaskDefinitionId.ToString() },
            { "task.version", context.BlueprintNode.ResolvedVersion.ToString() },
            { "process.outcome", outcome }
        };

        return tags;
    }
}
