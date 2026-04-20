using System.Text.Json;
using Backend.Database.Relational;
using Backend.Models.Observability;
using Backend.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Backend.API.Observability;

public sealed class ObservabilityQueryService
{
    private const int SummaryLogLimit = 20000;
    private readonly WorkflowDbContext _dbContext;
    private readonly LokiQueryClient _lokiQueryClient;
    private readonly PrometheusQueryClient _prometheusQueryClient;

    public ObservabilityQueryService(
        WorkflowDbContext dbContext,
        LokiQueryClient lokiQueryClient,
        PrometheusQueryClient prometheusQueryClient)
    {
        _dbContext = dbContext;
        _lokiQueryClient = lokiQueryClient;
        _prometheusQueryClient = prometheusQueryClient;
    }

    public async Task<WorkflowObservabilitySummaryViewModel> GetWorkflowSummaryAsync(
        int? blueprintDefinitionId,
        string? window,
        CancellationToken cancellationToken)
    {
        var range = ResolveWindow(window);
        var workflowEvents = await GetWorkflowLifecycleEventsAsync(range.From, range.To, cancellationToken);
        if (blueprintDefinitionId.HasValue)
        {
            workflowEvents = workflowEvents
                .Where(entry => entry.BlueprintDefinitionId == blueprintDefinitionId.Value)
                .ToList();
        }

        var terminalRuns = workflowEvents
            .Where(entry => entry.EventName is "workflow.completed" or "workflow.failed")
            .GroupBy(entry => entry.WorkflowInstanceId)
            .Select(group => group.OrderByDescending(entry => entry.Timestamp).First())
            .ToList();

        return new WorkflowObservabilitySummaryViewModel
        {
            Window = range.Window,
            From = range.From,
            To = range.To,
            SuccessfulCount = terminalRuns.Count(entry => entry.EventName == "workflow.completed"),
            FailedCount = terminalRuns.Count(entry => entry.EventName == "workflow.failed"),
        };
    }

    public async Task<NodeObservabilitySummaryViewModel> GetNodeSummaryAsync(
        int blueprintDefinitionId,
        string? window,
        CancellationToken cancellationToken)
    {
        var range = ResolveWindow(window);
        var latestBlueprintVersion = await _dbContext.BlueprintVersions
            .Where(version => version.BlueprintDefinitionId == blueprintDefinitionId)
            .OrderByDescending(version => version.Version)
            .Include(version => version.TaskInstances)
                .ThenInclude(taskInstance => taskInstance.TaskDefinition)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (latestBlueprintVersion is null)
        {
            throw new InvalidOperationException($"Blueprint {blueprintDefinitionId} was not found.");
        }

        var taskEvents = await GetTaskLifecycleEventsAsync(range.From, range.To, cancellationToken);
        var latestVersionTaskEvents = taskEvents
            .Where(entry =>
                entry.BlueprintDefinitionId == blueprintDefinitionId &&
                entry.BlueprintVersionId == latestBlueprintVersion.Id &&
                entry.EventName is "task.completed" or "task.failed")
            .GroupBy(entry => new { entry.WorkflowTaskInstanceId, entry.EventName })
            .Select(group => group.OrderByDescending(entry => entry.Timestamp).First())
            .ToList();

        var summaries = latestBlueprintVersion.TaskInstances
            .Select(task =>
            {
                var matchingEvents = latestVersionTaskEvents
                    .Where(entry => entry.BlueprintTaskId == task.Id)
                    .ToList();

                return new NodeObservabilitySummaryItemViewModel
                {
                    BlueprintTaskId = task.Id,
                    TaskDefinitionId = task.TaskDefinitionId,
                    TaskName = task.TaskDefinition.Name,
                    SuccessfulCount = matchingEvents.Count(entry => entry.EventName == "task.completed"),
                    FailedCount = matchingEvents.Count(entry => entry.EventName == "task.failed"),
                };
            })
            .OrderByDescending(summary => summary.TotalCount)
            .ThenBy(summary => summary.TaskName)
            .ToList();

        return new NodeObservabilitySummaryViewModel
        {
            Window = range.Window,
            From = range.From,
            To = range.To,
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintVersionId = latestBlueprintVersion.Id,
            BlueprintVersionNumber = latestBlueprintVersion.Version,
            BlueprintVersionName = latestBlueprintVersion.Name,
            Nodes = summaries,
        };
    }

    public async Task<WorkflowRecentRunsViewModel> GetRecentRunsAsync(
        int? blueprintDefinitionId,
        string? window,
        int limit,
        CancellationToken cancellationToken)
    {
        var range = ResolveWindow(window);
        var workflowEvents = await GetWorkflowLifecycleEventsAsync(range.From, range.To, cancellationToken);
        if (blueprintDefinitionId.HasValue)
        {
            workflowEvents = workflowEvents
                .Where(entry => entry.BlueprintDefinitionId == blueprintDefinitionId.Value)
                .ToList();
        }

        var runs = workflowEvents
            .Where(entry => entry.EventName is "workflow.completed" or "workflow.failed")
            .GroupBy(entry => entry.WorkflowInstanceId)
            .Select(group => group.OrderByDescending(entry => entry.Timestamp).First())
            .OrderByDescending(entry => entry.Timestamp)
            .Take(Math.Clamp(limit, 1, 250))
            .Select(entry => new RecentWorkflowExecutionViewModel
            {
                WorkflowInstanceId = entry.WorkflowInstanceId,
                BlueprintDefinitionId = entry.BlueprintDefinitionId,
                BlueprintVersionId = entry.BlueprintVersionId,
                BlueprintVersionNumber = entry.BlueprintVersionNumber,
                BlueprintVersionName = entry.BlueprintVersionName,
                Status = entry.Status,
                OccurredAt = entry.Timestamp,
                FailureMessage = entry.FailureMessage,
            })
            .ToList();

        return new WorkflowRecentRunsViewModel
        {
            Window = range.Window,
            From = range.From,
            To = range.To,
            Runs = runs,
        };
    }

    public async Task<NodeLogBatchViewModel> GetNodeLogsAsync(
        Guid workflowInstanceId,
        Guid runtimeTaskInstanceId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var start = ResolveLogStart(from, cursor);
        var end = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var nodeLogs = await _lokiQueryClient.QueryRangeAsync(
            BuildNodeLogQuery(runtimeTaskInstanceId),
            start,
            end,
            normalizedLimit,
            LokiQueryDirection.Forward,
            cancellationToken);

        if (nodeLogs.Count == 0)
        {
            nodeLogs = await _lokiQueryClient.QueryRangeAsync(
                BuildNodeLogFallbackQuery(runtimeTaskInstanceId),
                start,
                end,
                normalizedLimit,
                LokiQueryDirection.Forward,
                cancellationToken);
        }

        var entries = nodeLogs
            .Where(record => RecordMatchesNode(record, workflowInstanceId, runtimeTaskInstanceId))
            .Select(record => new NodeLogEntryViewModel
            {
                Cursor = record.Cursor,
                Timestamp = record.Timestamp,
                Message = FormatNodeLogMessage(record.Message),
                ServiceName = record.Labels.TryGetValue("service_name", out var serviceName) ? serviceName : null,
                Level = record.Labels.TryGetValue("detected_level", out var level) ? level : null,
            })
            .ToList();

        return new NodeLogBatchViewModel
        {
            Entries = entries,
            NextCursor = entries.Count > 0 ? entries[^1].Cursor : cursor,
        };
    }

    public async Task<WorkflowLogBatchViewModel> GetWorkflowLogsAsync(
        Guid workflowInstanceId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var start = ResolveLogStart(from, cursor);
        var end = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var workflowLogs = await _lokiQueryClient.QueryRangeAsync(
            BuildWorkflowLogQuery(workflowInstanceId),
            start,
            end,
            normalizedLimit,
            LokiQueryDirection.Forward,
            cancellationToken);

        if (workflowLogs.Count == 0)
        {
            workflowLogs = await _lokiQueryClient.QueryRangeAsync(
                BuildWorkflowLogFallbackQuery(workflowInstanceId),
                start,
                end,
                normalizedLimit,
                LokiQueryDirection.Forward,
                cancellationToken);
        }

        var entries = workflowLogs
            .Where(record => RecordMatchesWorkflow(record, workflowInstanceId))
            .Select(MapWorkflowLogEntry)
            .ToList();

        return new WorkflowLogBatchViewModel
        {
            Entries = entries,
            NextCursor = entries.Count > 0 ? entries[^1].Cursor : cursor,
        };
    }

    public Task<JsonDocument> QueryPrometheusAsync(string expression, CancellationToken cancellationToken) =>
        _prometheusQueryClient.QueryAsync(expression, cancellationToken);

    private async Task<List<WorkflowLifecycleLogEntry>> GetWorkflowLifecycleEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var rawLogs = await _lokiQueryClient.QueryRangeAsync(
            "{service_name=\"backend-engine\"} |= \"\\\"eventCategory\\\":\\\"workflow.lifecycle\\\"\"",
            from,
            to,
            SummaryLogLimit,
            LokiQueryDirection.Backward,
            cancellationToken);

        return rawLogs
            .Select(record => TryParseWorkflowLifecycleLogEntry(record.Message, record.Timestamp))
            .Where(entry => entry is not null)
            .Cast<WorkflowLifecycleLogEntry>()
            .ToList();
    }

    private async Task<List<TaskLifecycleLogEntry>> GetTaskLifecycleEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var rawLogs = await _lokiQueryClient.QueryRangeAsync(
            "{service_name=\"backend-engine\"} |= \"\\\"eventCategory\\\":\\\"task.lifecycle\\\"\"",
            from,
            to,
            SummaryLogLimit,
            LokiQueryDirection.Backward,
            cancellationToken);

        return rawLogs
            .Select(record => TryParseTaskLifecycleLogEntry(record.Message, record.Timestamp))
            .Where(entry => entry is not null)
            .Cast<TaskLifecycleLogEntry>()
            .ToList();
    }

    private static (string Window, DateTimeOffset From, DateTimeOffset To) ResolveWindow(string? window)
    {
        var normalizedWindow = string.IsNullOrWhiteSpace(window) ? "1h" : window.Trim().ToLowerInvariant();
        var duration = normalizedWindow switch
        {
            "15m" => TimeSpan.FromMinutes(15),
            "1h" => TimeSpan.FromHours(1),
            "6h" => TimeSpan.FromHours(6),
            "24h" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            _ => TimeSpan.FromHours(1),
        };

        var to = DateTimeOffset.UtcNow;
        return (normalizedWindow, to - duration, to);
    }

    private static DateTimeOffset ResolveLogStart(DateTimeOffset? from, string? cursor)
    {
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            return LokiQueryClient.FromUnixNanoseconds(cursor).AddTicks(1);
        }

        return from?.ToUniversalTime() ?? DateTimeOffset.UtcNow.AddMinutes(-15);
    }

    private static string BuildNodeLogQuery(Guid runtimeTaskInstanceId) =>
        $"{{workflow_task_instance_id=\"{runtimeTaskInstanceId:D}\"}}";

    private static string BuildNodeLogFallbackQuery(Guid runtimeTaskInstanceId) =>
        $"{{service_name=~\".+\"}} |= \"{runtimeTaskInstanceId:D}\"";

    private static string BuildWorkflowLogQuery(Guid workflowInstanceId) =>
        $"{{workflow_instance_id=\"{workflowInstanceId:D}\"}}";

    private static string BuildWorkflowLogFallbackQuery(Guid workflowInstanceId) =>
        $"{{service_name=~\".+\"}} |= \"{workflowInstanceId:D}\"";

    private static bool RecordMatchesNode(
        LokiLogRecord record,
        Guid workflowInstanceId,
        Guid runtimeTaskInstanceId)
    {
        if (record.Labels.TryGetValue("workflow_task_instance_id", out var labeledTaskInstanceId) &&
            Guid.TryParse(labeledTaskInstanceId, out var parsedTaskInstanceId) &&
            parsedTaskInstanceId == runtimeTaskInstanceId)
        {
            if (!record.Labels.TryGetValue("workflow_instance_id", out var labeledWorkflowInstanceId) ||
                !Guid.TryParse(labeledWorkflowInstanceId, out var parsedWorkflowInstanceId) ||
                parsedWorkflowInstanceId == workflowInstanceId)
            {
                return true;
            }
        }

        if (record.Message.Contains(runtimeTaskInstanceId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return record.Message.Contains(runtimeTaskInstanceId.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool RecordMatchesWorkflow(
        LokiLogRecord record,
        Guid workflowInstanceId)
    {
        if (record.Labels.TryGetValue("workflow_instance_id", out var labeledWorkflowInstanceId) &&
            Guid.TryParse(labeledWorkflowInstanceId, out var parsedWorkflowInstanceId) &&
            parsedWorkflowInstanceId == workflowInstanceId)
        {
            return true;
        }

        if (record.Message.Contains(workflowInstanceId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return record.Message.Contains(workflowInstanceId.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }

    private static WorkflowLogEntryViewModel MapWorkflowLogEntry(LokiLogRecord record)
    {
        var taskLifecycle = TryParseTaskLifecycleLogEntry(record.Message, record.Timestamp);
        if (taskLifecycle is not null)
        {
            return new WorkflowLogEntryViewModel
            {
                Cursor = record.Cursor,
                Timestamp = record.Timestamp,
                Message = FormatNodeLogMessage(record.Message),
                WorkflowTaskInstanceId = taskLifecycle.WorkflowTaskInstanceId,
                BlueprintTaskId = taskLifecycle.BlueprintTaskId,
                TaskName = taskLifecycle.TaskName,
                PluginName = taskLifecycle.PluginName,
                ServiceName = record.Labels.TryGetValue("service_name", out var taskServiceName) ? taskServiceName : null,
                Level = record.Labels.TryGetValue("detected_level", out var taskLevel) ? taskLevel : null,
            };
        }

        var workflowLifecycle = TryParseWorkflowLifecycleLogEntry(record.Message, record.Timestamp);
        if (workflowLifecycle is not null)
        {
            return new WorkflowLogEntryViewModel
            {
                Cursor = record.Cursor,
                Timestamp = record.Timestamp,
                Message = FormatNodeLogMessage(record.Message),
                WorkflowTaskInstanceId = TryGetGuidLabel(record.Labels, "workflow_task_instance_id"),
                BlueprintTaskId = TryGetGuidLabel(record.Labels, "blueprint_task_id"),
                TaskName = null,
                PluginName = record.Labels.TryGetValue("plugin_name", out var pluginName) ? pluginName : null,
                ServiceName = record.Labels.TryGetValue("service_name", out var workflowServiceName) ? workflowServiceName : null,
                Level = record.Labels.TryGetValue("detected_level", out var workflowLevel) ? workflowLevel : null,
            };
        }

        var fallbackPluginName = record.Labels.TryGetValue("plugin_name", out var genericPluginName)
            ? genericPluginName
            : null;

        return new WorkflowLogEntryViewModel
        {
            Cursor = record.Cursor,
            Timestamp = record.Timestamp,
            Message = record.Message,
            WorkflowTaskInstanceId = TryGetGuidLabel(record.Labels, "workflow_task_instance_id"),
            BlueprintTaskId = TryGetGuidLabel(record.Labels, "blueprint_task_id"),
            TaskName = fallbackPluginName,
            PluginName = fallbackPluginName,
            ServiceName = record.Labels.TryGetValue("service_name", out var fallbackServiceName) ? fallbackServiceName : null,
            Level = record.Labels.TryGetValue("detected_level", out var fallbackLevel) ? fallbackLevel : null,
        };
    }

    private static string FormatNodeLogMessage(string rawMessage)
    {
        var taskLifecycle = TryParseTaskLifecycleLogEntry(rawMessage, timestampFallback: null);
        if (taskLifecycle is not null)
        {
            return taskLifecycle.EventName switch
            {
                "task.started" => $"Task started: {taskLifecycle.TaskName} ({taskLifecycle.TaskVersion})",
                "task.completed" => $"Task completed: {taskLifecycle.TaskName} ({taskLifecycle.TaskVersion}) in {taskLifecycle.DurationMs?.ToString("0")}ms",
                "task.failed" => $"Task failed: {taskLifecycle.TaskName} ({taskLifecycle.TaskVersion}) - {taskLifecycle.FailureMessage}",
                _ => rawMessage,
            };
        }

        var workflowLifecycle = TryParseWorkflowLifecycleLogEntry(rawMessage, timestampFallback: null);
        if (workflowLifecycle is not null)
        {
            return workflowLifecycle.EventName switch
            {
                "workflow.started" => $"Workflow started: {workflowLifecycle.BlueprintVersionName}",
                "workflow.completed" => $"Workflow completed: {workflowLifecycle.BlueprintVersionName}",
                "workflow.failed" => $"Workflow failed: {workflowLifecycle.BlueprintVersionName} - {workflowLifecycle.FailureMessage}",
                _ => rawMessage,
            };
        }

        return rawMessage;
    }

    private static WorkflowLifecycleLogEntry? TryParseWorkflowLifecycleLogEntry(
        string rawMessage,
        DateTimeOffset? timestampFallback)
    {
        try
        {
            using var document = JsonDocument.Parse(rawMessage);
            var root = document.RootElement;
            if (!TryGetString(root, "eventCategory", out var eventCategory) ||
                !string.Equals(eventCategory, "workflow.lifecycle", StringComparison.Ordinal))
            {
                return null;
            }

            return new WorkflowLifecycleLogEntry
            {
                EventCategory = eventCategory,
                EventName = GetRequiredString(root, "eventName"),
                Timestamp = TryGetDateTimeOffset(root, "timestamp", out var timestamp)
                    ? timestamp
                    : timestampFallback ?? DateTimeOffset.UtcNow,
                WorkflowInstanceId = GetRequiredGuid(root, "workflowInstanceId"),
                BlueprintDefinitionId = GetRequiredInt32(root, "blueprintDefinitionId"),
                BlueprintVersionId = GetRequiredInt32(root, "blueprintVersionId"),
                BlueprintVersionNumber = GetRequiredInt32(root, "blueprintVersionNumber"),
                BlueprintVersionName = GetRequiredString(root, "blueprintVersionName"),
                Status = GetRequiredString(root, "status"),
                ExecutionOutcome = GetOptionalString(root, "executionOutcome"),
                FailureMessage = GetOptionalString(root, "failureMessage"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static TaskLifecycleLogEntry? TryParseTaskLifecycleLogEntry(
        string rawMessage,
        DateTimeOffset? timestampFallback)
    {
        try
        {
            using var document = JsonDocument.Parse(rawMessage);
            var root = document.RootElement;
            if (!TryGetString(root, "eventCategory", out var eventCategory) ||
                !string.Equals(eventCategory, "task.lifecycle", StringComparison.Ordinal))
            {
                return null;
            }

            return new TaskLifecycleLogEntry
            {
                EventCategory = eventCategory,
                EventName = GetRequiredString(root, "eventName"),
                Timestamp = TryGetDateTimeOffset(root, "timestamp", out var timestamp)
                    ? timestamp
                    : timestampFallback ?? DateTimeOffset.UtcNow,
                WorkflowInstanceId = GetRequiredGuid(root, "workflowInstanceId"),
                WorkflowTaskInstanceId = GetRequiredGuid(root, "workflowTaskInstanceId"),
                BlueprintTaskId = GetRequiredGuid(root, "blueprintTaskId"),
                BlueprintDefinitionId = GetRequiredInt32(root, "blueprintDefinitionId"),
                BlueprintVersionId = GetRequiredInt32(root, "blueprintVersionId"),
                BlueprintVersionNumber = GetRequiredInt32(root, "blueprintVersionNumber"),
                BlueprintVersionName = GetRequiredString(root, "blueprintVersionName"),
                TaskName = GetRequiredString(root, "taskName"),
                PluginName = GetRequiredString(root, "pluginName"),
                TaskDefinitionId = GetRequiredInt32(root, "taskDefinitionId"),
                TaskVersionId = GetRequiredInt32(root, "taskVersionId"),
                TaskVersion = GetRequiredString(root, "taskVersion"),
                Status = GetRequiredString(root, "status"),
                ExecutionOutcome = GetOptionalString(root, "executionOutcome"),
                DurationMs = TryGetDouble(root, "durationMs", out var durationMs) ? durationMs : null,
                FailureMessage = GetOptionalString(root, "failureMessage"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string GetRequiredString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? throw new InvalidOperationException($"Property '{propertyName}' is null.")
            : throw new InvalidOperationException($"Property '{propertyName}' was not present.");

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static int GetRequiredInt32(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : throw new InvalidOperationException($"Property '{propertyName}' was not present.");

    private static Guid GetRequiredGuid(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        Guid.TryParse(property.GetString(), out var value)
            ? value
            : throw new InvalidOperationException($"Property '{propertyName}' was not present.");

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } stringValue)
        {
            value = stringValue;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetDateTimeOffset(JsonElement root, string propertyName, out DateTimeOffset value)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(property.GetString(), out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetDouble(JsonElement root, string propertyName, out double value)
    {
        if (root.TryGetProperty(propertyName, out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private static Guid? TryGetGuidLabel(
        IReadOnlyDictionary<string, string> labels,
        string labelName) =>
        labels.TryGetValue(labelName, out var labelValue) &&
        Guid.TryParse(labelValue, out var parsed)
            ? parsed
            : null;
}
