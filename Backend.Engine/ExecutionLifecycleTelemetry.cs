using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Backend.Models.Observability;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed class ExecutionLifecycleTelemetry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("WorkflowBuilder.Engine.Execution", "1.0.0");
    private static readonly Counter<long> TaskExecutionsStarted = Meter.CreateCounter<long>("workflow_task_started_total");
    private static readonly Counter<long> TaskExecutionsCompleted = Meter.CreateCounter<long>("workflow_task_completed_total");
    private static readonly Counter<long> TaskExecutionsFailed = Meter.CreateCounter<long>("workflow_task_failed_total");
    private static readonly Histogram<double> TaskExecutionDurationMs = Meter.CreateHistogram<double>("workflow_task_duration_ms");

    private readonly ILogger<ExecutionLifecycleTelemetry> _logger;

    public ExecutionLifecycleTelemetry(ILogger<ExecutionLifecycleTelemetry> logger)
    {
        _logger = logger;
    }

    public void LogWorkflowStarted(JobContext context, DateTimeOffset timestamp) =>
        WriteWorkflowLifecycle(
            context,
            new WorkflowLifecycleLogEntry
            {
                EventCategory = "workflow.lifecycle",
                EventName = "workflow.started",
                Timestamp = timestamp,
                WorkflowInstanceId = context.WorkflowInstanceId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintVersionId = context.WorkflowBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                BlueprintVersionName = context.BlueprintVersionName,
                Status = "InProgress",
                ExecutionOutcome = null,
                FailureMessage = null,
            });

    public void LogWorkflowCompleted(JobContext context, DateTimeOffset timestamp) =>
        WriteWorkflowLifecycle(
            context,
            new WorkflowLifecycleLogEntry
            {
                EventCategory = "workflow.lifecycle",
                EventName = "workflow.completed",
                Timestamp = timestamp,
                WorkflowInstanceId = context.WorkflowInstanceId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintVersionId = context.WorkflowBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                BlueprintVersionName = context.BlueprintVersionName,
                Status = "Completed",
                ExecutionOutcome = "Succeeded",
                FailureMessage = null,
            });

    public void LogWorkflowFailed(JobContext context, DateTimeOffset timestamp, string? failureMessage) =>
        WriteWorkflowLifecycle(
            context,
            new WorkflowLifecycleLogEntry
            {
                EventCategory = "workflow.lifecycle",
                EventName = "workflow.failed",
                Timestamp = timestamp,
                WorkflowInstanceId = context.WorkflowInstanceId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintVersionId = context.WorkflowBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                BlueprintVersionName = context.BlueprintVersionName,
                Status = "Failed",
                ExecutionOutcome = "Failed",
                FailureMessage = failureMessage,
            });

    public void LogTaskStarted(JobContext context, DateTimeOffset timestamp)
    {
        TaskExecutionsStarted.Add(1, BuildTaskMetricTags(context, outcome: "started"));
        WriteTaskLifecycle(
            context,
            new TaskLifecycleLogEntry
            {
                EventCategory = "task.lifecycle",
                EventName = "task.started",
                Timestamp = timestamp,
                WorkflowInstanceId = context.WorkflowInstanceId,
                WorkflowTaskInstanceId = context.WorkflowTaskInstanceId,
                BlueprintTaskId = context.BlueprintTaskId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintVersionId = context.WorkflowBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                BlueprintVersionName = context.BlueprintVersionName,
                TaskName = context.TaskName,
                PluginName = context.PluginName,
                TaskDefinitionId = context.TaskDefinitionId,
                TaskVersionId = context.ResolvedTaskVersionId,
                TaskVersion = context.WorkflowTask.ResolvedVersion.ToString(),
                Status = "InProgress",
                ExecutionOutcome = null,
                DurationMs = null,
                FailureMessage = null,
            });
    }

    public void LogTaskCompleted(JobContext context, PluginExecutionResult result)
    {
        var duration = (result.CompletedAt - result.StartedAt).TotalMilliseconds;
        var tags = BuildTaskMetricTags(context, outcome: "succeeded");
        TaskExecutionsCompleted.Add(1, tags);
        TaskExecutionDurationMs.Record(duration, tags);

        WriteTaskLifecycle(
            context,
            new TaskLifecycleLogEntry
            {
                EventCategory = "task.lifecycle",
                EventName = "task.completed",
                Timestamp = result.CompletedAt,
                WorkflowInstanceId = context.WorkflowInstanceId,
                WorkflowTaskInstanceId = context.WorkflowTaskInstanceId,
                BlueprintTaskId = context.BlueprintTaskId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintVersionId = context.WorkflowBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                BlueprintVersionName = context.BlueprintVersionName,
                TaskName = context.TaskName,
                PluginName = context.PluginName,
                TaskDefinitionId = context.TaskDefinitionId,
                TaskVersionId = context.ResolvedTaskVersionId,
                TaskVersion = context.WorkflowTask.ResolvedVersion.ToString(),
                Status = "Completed",
                ExecutionOutcome = "Succeeded",
                DurationMs = duration,
                FailureMessage = null,
            });
    }

    public void LogTaskFailed(JobContext context, PluginExecutionResult result)
    {
        var duration = (result.CompletedAt - result.StartedAt).TotalMilliseconds;
        var tags = BuildTaskMetricTags(context, outcome: "failed");
        TaskExecutionsFailed.Add(1, tags);
        TaskExecutionDurationMs.Record(duration, tags);

        WriteTaskLifecycle(
            context,
            new TaskLifecycleLogEntry
            {
                EventCategory = "task.lifecycle",
                EventName = "task.failed",
                Timestamp = result.CompletedAt,
                WorkflowInstanceId = context.WorkflowInstanceId,
                WorkflowTaskInstanceId = context.WorkflowTaskInstanceId,
                BlueprintTaskId = context.BlueprintTaskId,
                BlueprintDefinitionId = context.BlueprintDefinitionId,
                BlueprintVersionId = context.WorkflowBlueprintId,
                BlueprintVersionNumber = context.BlueprintVersionNumber,
                BlueprintVersionName = context.BlueprintVersionName,
                TaskName = context.TaskName,
                PluginName = context.PluginName,
                TaskDefinitionId = context.TaskDefinitionId,
                TaskVersionId = context.ResolvedTaskVersionId,
                TaskVersion = context.WorkflowTask.ResolvedVersion.ToString(),
                Status = "Failed",
                ExecutionOutcome = "Failed",
                DurationMs = duration,
                FailureMessage = result.ErrorMessage,
            });
    }

    private void WriteWorkflowLifecycle(JobContext context, WorkflowLifecycleLogEntry entry)
    {
        using var scope = _logger.BeginScope(BuildScope(context, entry.EventName, entry.ExecutionOutcome));
        _logger.LogInformation("{LifecycleEvent}", JsonSerializer.Serialize(entry, JsonOptions));
    }

    private void WriteTaskLifecycle(JobContext context, TaskLifecycleLogEntry entry)
    {
        using var scope = _logger.BeginScope(BuildScope(context, entry.EventName, entry.ExecutionOutcome));
        _logger.LogInformation("{LifecycleEvent}", JsonSerializer.Serialize(entry, JsonOptions));
    }

    private static Dictionary<string, object?> BuildScope(
        JobContext context,
        string eventName,
        string? executionOutcome) =>
        new(StringComparer.Ordinal)
        {
            ["workflow.instance_id"] = context.WorkflowInstanceId,
            ["workflow.task_instance_id"] = context.WorkflowTaskInstanceId,
            ["blueprint.definition_id"] = context.BlueprintDefinitionId,
            ["blueprint.version_id"] = context.WorkflowBlueprintId,
            ["blueprint.task_id"] = context.BlueprintTaskId,
            ["task.definition_id"] = context.TaskDefinitionId,
            ["task.version_id"] = context.ResolvedTaskVersionId,
            ["task.version"] = context.WorkflowTask.ResolvedVersion.ToString(),
            ["plugin.name"] = context.PluginName,
            ["event.name"] = eventName,
            ["execution.outcome"] = executionOutcome
        };

    private static TagList BuildTaskMetricTags(JobContext context, string outcome)
    {
        var tags = new TagList
        {
            { "plugin.name", context.PluginName },
            { "task.definition_id", context.TaskDefinitionId.ToString() },
            { "task.version", context.WorkflowTask.ResolvedVersion.ToString() },
            { "execution.outcome", outcome }
        };

        return tags;
    }
}
