using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Backend.Models.Observability;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed class ProcessLifecycleTelemetry
{
    public const string ActivitySourceName = "WorkflowBuilder.Engine.Processes";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    private static readonly Meter Meter = new(ActivitySourceName, "1.0.0");
    private static readonly Counter<long> ProcessStepsStarted = Meter.CreateCounter<long>("process_step_started_total");
    private static readonly Counter<long> ProcessStepsCompleted = Meter.CreateCounter<long>("process_step_completed_total");
    private static readonly Counter<long> ProcessStepsFailed = Meter.CreateCounter<long>("process_step_failed_total");
    private static readonly Histogram<double> ProcessStepDurationMs = Meter.CreateHistogram<double>("process_step_duration_ms");
    private static readonly Histogram<double> ProcessStepQueueWaitMs = Meter.CreateHistogram<double>("process_step_queue_wait_ms");
    private static readonly Histogram<double> ProcessStepClaimMs = Meter.CreateHistogram<double>("process_step_claim_ms");
    private static readonly Histogram<double> ProcessStepAgentEnqueueMs = Meter.CreateHistogram<double>("process_step_agent_enqueue_ms");
    private static readonly Histogram<double> ProcessStepPluginStartDelayMs = Meter.CreateHistogram<double>("process_step_plugin_start_delay_ms");
    private static readonly Histogram<double> ProcessStepFinalizeMs = Meter.CreateHistogram<double>("process_step_finalize_ms");
    private static readonly Histogram<double> ProcessStepRecordedDurationMs = Meter.CreateHistogram<double>("process_step_recorded_duration_ms");
    private static readonly Histogram<double> PluginHydrationMs = Meter.CreateHistogram<double>("plugin_hydration_ms");
    private static readonly Histogram<double> PluginExecuteMs = Meter.CreateHistogram<double>("plugin_execute_ms");
    private static readonly Histogram<long> CollectionIteratorItemCount = Meter.CreateHistogram<long>("collection_iterator_item_count");
    private static readonly Histogram<long> CollectionIteratorBatchCount = Meter.CreateHistogram<long>("collection_iterator_batch_count");
    private static readonly Histogram<double> CollectionIteratorEnqueueMs = Meter.CreateHistogram<double>("collection_iterator_enqueue_ms");
    private static readonly Counter<long> CollectionIteratorSkippedEmpty = Meter.CreateCounter<long>("collection_iterator_skipped_empty_total");
    private static readonly Counter<long> CollectionIteratorLimitFailures = Meter.CreateCounter<long>("collection_iterator_limit_failure_total");

    private readonly ILogger<ProcessLifecycleTelemetry> _logger;

    public ProcessLifecycleTelemetry(ILogger<ProcessLifecycleTelemetry> logger)
    {
        _logger = logger;
    }

    public Activity? StartStepActivity(StepExecutionContext context)
    {
        var activity = ActivitySource.StartActivity(
            "process.step",
            ActivityKind.Consumer);

        if (activity is null)
        {
            return null;
        }

        activity.SetTag("process.id", context.ProcessId.ToString());
        activity.SetTag("process.step_id", context.ProcessStepId.ToString());
        activity.SetTag("blueprint.definition_id", context.BlueprintDefinitionId);
        activity.SetTag("blueprint.version_id", context.ExecutableBlueprintId);
        activity.SetTag("blueprint.version_number", context.BlueprintVersionNumber);
        activity.SetTag("blueprint.node_id", context.BlueprintNodeId.ToString());
        activity.SetTag("task.definition_id", context.TaskDefinitionId);
        activity.SetTag("task.version_id", context.ResolvedTaskVersionId);
        activity.SetTag("task.version", context.BlueprintNode.ResolvedVersion.ToString());
        activity.SetTag("plugin.name", context.PluginName);
        activity.SetTag("event.name", "process.step");
        activity.SetTag("step.status", "InProgress");

        return activity;
    }

    public void StopStepActivity(
        StepExecutionContext context,
        PluginExecutionResult? result,
        Exception? exception)
    {
        var activity = context.ExecutionActivity;
        if (activity is null)
        {
            return;
        }

        if (result is not null)
        {
            var status = result.Succeeded ? "Completed" : "Failed";
            activity.SetTag("step.status", status);
            activity.SetTag("process.outcome", result.Succeeded ? "Succeeded" : "Failed");
            activity.SetTag("step.duration_ms", (result.CompletedAt - result.StartedAt).TotalMilliseconds);

            if (!result.Succeeded)
            {
                activity.SetStatus(ActivityStatusCode.Error, result.ErrorMessage);
            }
        }

        if (exception is not null)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.SetTag("exception.type", exception.GetType().FullName);
            activity.SetTag("exception.message", exception.Message);
        }

        activity.Stop();
        context.ExecutionActivity = null;
    }

    public void LogProcessStarted(StepExecutionContext context, DateTimeOffset timestamp) =>
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

    public void LogProcessCompleted(StepExecutionContext context, DateTimeOffset timestamp) =>
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

    public void LogProcessFailed(StepExecutionContext context, DateTimeOffset timestamp, string? failureMessage) =>
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

    public void LogStepStarted(StepExecutionContext context, DateTimeOffset timestamp)
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

    public void RecordQueueWait(StepExecutionContext context, DateTimeOffset enqueuedAt, DateTimeOffset receivedAt)
    {
        var elapsed = receivedAt - enqueuedAt;
        ProcessStepQueueWaitMs.Record(Math.Max(0, elapsed.TotalMilliseconds), BuildStepMetricTags(context, outcome: "received"));
    }

    public void RecordStepClaim(StepExecutionContext context, TimeSpan elapsed, bool claimed)
    {
        var tags = BuildStepMetricTags(context, claimed ? "claimed" : "claim_skipped");
        ProcessStepClaimMs.Record(Math.Max(0, elapsed.TotalMilliseconds), tags);
    }

    public void RecordAgentEnqueue(StepExecutionContext context, TimeSpan elapsed, bool queued)
    {
        var tags = BuildStepMetricTags(context, queued ? "queued" : "queue_rejected");
        ProcessStepAgentEnqueueMs.Record(Math.Max(0, elapsed.TotalMilliseconds), tags);
    }

    public void RecordPluginStartDelay(StepExecutionContext context, TimeSpan elapsed)
    {
        ProcessStepPluginStartDelayMs.Record(
            Math.Max(0, elapsed.TotalMilliseconds),
            BuildStepMetricTags(context, outcome: "started"));
    }

    public void RecordFinalization(StepExecutionContext context, TimeSpan elapsed, bool succeeded)
    {
        ProcessStepFinalizeMs.Record(
            Math.Max(0, elapsed.TotalMilliseconds),
            BuildStepMetricTags(context, succeeded ? "succeeded" : "failed"));
    }

    public void RecordRecordedStepDuration(StepExecutionContext context, DateTimeOffset completedAt)
    {
        if (context.ClaimedAt is not DateTimeOffset claimedAt)
        {
            return;
        }

        ProcessStepRecordedDurationMs.Record(
            Math.Max(0, (completedAt - claimedAt).TotalMilliseconds),
            BuildStepMetricTags(context, outcome: "completed"));
    }

    public void RecordPluginPhaseTimings(StepExecutionContext context, IReadOnlyDictionary<string, double>? timingsMs)
    {
        if (timingsMs is null)
        {
            return;
        }

        var tags = BuildStepMetricTags(context, outcome: "succeeded");
        if (timingsMs.TryGetValue("hydrationMs", out var hydrationMs))
        {
            PluginHydrationMs.Record(Math.Max(0, hydrationMs), tags);
        }

        if (timingsMs.TryGetValue("executeMs", out var executeMs))
        {
            PluginExecuteMs.Record(Math.Max(0, executeMs), tags);
        }
    }

    public void RecordCollectionIteratorFanOut(
        StepExecutionContext context,
        int itemCount,
        int queuedStepCount,
        int batchSize,
        TimeSpan enqueueElapsed)
    {
        var tags = BuildStepMetricTags(context, outcome: "collection_iterator_fan_out");
        var effectiveBatchSize = Math.Max(1, batchSize);
        var batchCount = queuedStepCount == 0
            ? 0
            : (int)Math.Ceiling(queuedStepCount / (double)effectiveBatchSize);
        CollectionIteratorItemCount.Record(itemCount, tags);
        CollectionIteratorBatchCount.Record(batchCount, tags);
        CollectionIteratorEnqueueMs.Record(Math.Max(0, enqueueElapsed.TotalMilliseconds), tags);
    }

    public void RecordCollectionIteratorSkippedEmpty(StepExecutionContext context) =>
        CollectionIteratorSkippedEmpty.Add(1, BuildStepMetricTags(context, outcome: "collection_iterator_empty"));

    public void RecordCollectionIteratorLimitFailure(
        StepExecutionContext context,
        int itemCount,
        int maxItems)
    {
        var tags = BuildStepMetricTags(context, outcome: "collection_iterator_limit_failure");
        tags.Add("collection_iterator.item_count", itemCount.ToString());
        tags.Add("collection_iterator.max_items", maxItems.ToString());
        CollectionIteratorLimitFailures.Add(1, tags);
    }

    public void LogStepCompleted(StepExecutionContext context, PluginExecutionResult result)
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

    public void LogStepFailed(StepExecutionContext context, PluginExecutionResult result)
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

    private void WriteProcessLifecycle(StepExecutionContext context, ProcessLifecycleLogEntry entry)
    {
        using var scope = _logger.BeginScope(BuildScope(context, entry.EventName, entry.ProcessOutcome));
        _logger.LogInformation("{LifecycleEvent}", JsonSerializer.Serialize(entry, JsonOptions));
    }

    private void WriteStepLifecycle(StepExecutionContext context, ProcessStepLifecycleLogEntry entry)
    {
        using var scope = _logger.BeginScope(BuildScope(context, entry.EventName, entry.ProcessOutcome));
        _logger.LogInformation("{LifecycleEvent}", JsonSerializer.Serialize(entry, JsonOptions));
    }

    private static Dictionary<string, object?> BuildScope(
        StepExecutionContext context,
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

    private static TagList BuildStepMetricTags(StepExecutionContext context, string outcome)
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
