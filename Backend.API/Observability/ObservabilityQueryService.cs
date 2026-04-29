using System.Text.Json;
using Backend.Database.Relational;
using Backend.Models.Observability;
using Backend.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Backend.API.Observability;

public sealed class ObservabilityQueryService
{
    private const int SummaryLogLimit = 20000;
    private readonly BlueprintDbContext _dbContext;
    private readonly LokiQueryClient _lokiQueryClient;
    private readonly PrometheusQueryClient _prometheusQueryClient;

    public ObservabilityQueryService(
        BlueprintDbContext dbContext,
        LokiQueryClient lokiQueryClient,
        PrometheusQueryClient prometheusQueryClient)
    {
        _dbContext = dbContext;
        _lokiQueryClient = lokiQueryClient;
        _prometheusQueryClient = prometheusQueryClient;
    }

    public async Task<ProcessObservabilitySummaryViewModel> GetProcessSummaryAsync(
        int? blueprintDefinitionId,
        string? window,
        CancellationToken cancellationToken)
    {
        var range = ResolveWindow(window);
        var processEvents = await GetProcessLifecycleEventsAsync(range.From, range.To, cancellationToken);
        if (blueprintDefinitionId.HasValue)
        {
            processEvents = processEvents
                .Where(entry => entry.BlueprintDefinitionId == blueprintDefinitionId.Value)
                .ToList();
        }

        var terminalProcesses = processEvents
            .Where(entry => entry.EventName is "process.completed" or "process.failed")
            .GroupBy(entry => entry.ProcessId)
            .Select(group => group.OrderByDescending(entry => entry.Timestamp).First())
            .ToList();

        return new ProcessObservabilitySummaryViewModel
        {
            Window = range.Window,
            From = range.From,
            To = range.To,
            SuccessfulCount = terminalProcesses.Count(entry => entry.EventName == "process.completed"),
            FailedCount = terminalProcesses.Count(entry => entry.EventName == "process.failed"),
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
            .Include(version => version.BlueprintDefinition)
            .Include(version => version.BlueprintNodes)
                .ThenInclude(taskInstance => taskInstance.TaskDefinition)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(cancellationToken);

        if (latestBlueprintVersion is null)
        {
            throw new InvalidOperationException($"Blueprint {blueprintDefinitionId} was not found.");
        }

        var stepEvents = await GetStepLifecycleEventsAsync(range.From, range.To, cancellationToken);
        var latestVersionStepEvents = stepEvents
            .Where(entry =>
                entry.BlueprintDefinitionId == blueprintDefinitionId &&
                entry.BlueprintVersionId == latestBlueprintVersion.Id &&
                entry.EventName is "step.completed" or "step.failed")
            .GroupBy(entry => new { entry.ProcessStepId, entry.EventName })
            .Select(group => group.OrderByDescending(entry => entry.Timestamp).First())
            .ToList();

        var summaries = latestBlueprintVersion.BlueprintNodes
            .Select(task =>
            {
                var matchingEvents = latestVersionStepEvents
                    .Where(entry => entry.BlueprintNodeId == task.Id)
                    .ToList();

                return new NodeObservabilitySummaryItemViewModel
                {
                    BlueprintNodeId = task.Id,
                    TaskDefinitionId = task.TaskDefinitionId ?? 0,
                    TaskName = task.NodeKind == Backend.Database.Relational.Entities.BlueprintNodeKind.CollectionIterator
                        ? "Collection Iterator"
                        : task.TaskDefinition?.Name ?? "Task",
                    SuccessfulCount = matchingEvents.Count(entry => entry.EventName == "step.completed"),
                    FailedCount = matchingEvents.Count(entry => entry.EventName == "step.failed"),
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
            BlueprintName = latestBlueprintVersion.BlueprintDefinition.DisplayName,
            BlueprintVersionId = latestBlueprintVersion.Id,
            BlueprintVersionNumber = latestBlueprintVersion.Version,
            Nodes = summaries,
        };
    }

    public async Task<RecentProcessesViewModel> GetRecentProcessesAsync(
        int? blueprintDefinitionId,
        string? window,
        int limit,
        CancellationToken cancellationToken)
    {
        var range = ResolveWindow(window);
        var processEvents = await GetProcessLifecycleEventsAsync(range.From, range.To, cancellationToken);
        if (blueprintDefinitionId.HasValue)
        {
            processEvents = processEvents
                .Where(entry => entry.BlueprintDefinitionId == blueprintDefinitionId.Value)
                .ToList();
        }

        var processes = processEvents
            .Where(entry => entry.EventName is "process.completed" or "process.failed")
            .GroupBy(entry => entry.ProcessId)
            .Select(group => group.OrderByDescending(entry => entry.Timestamp).First())
            .OrderByDescending(entry => entry.Timestamp)
            .Take(Math.Clamp(limit, 1, 250))
            .Select(entry => new RecentProcessViewModel
            {
                ProcessId = entry.ProcessId,
                BlueprintDefinitionId = entry.BlueprintDefinitionId,
                BlueprintName = entry.BlueprintName,
                BlueprintVersionId = entry.BlueprintVersionId,
                BlueprintVersionNumber = entry.BlueprintVersionNumber,
                Status = entry.Status,
                OccurredAt = entry.Timestamp,
                FailureMessage = entry.FailureMessage,
            })
            .ToList();

        return new RecentProcessesViewModel
        {
            Window = range.Window,
            From = range.From,
            To = range.To,
            Processes = processes,
        };
    }

    public async Task<ProcessStepLogBatchViewModel> GetProcessStepLogsByRuntimeIdAsync(
        Guid processId,
        Guid runtimeProcessStepId,
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
            BuildProcessStepLogQuery(runtimeProcessStepId),
            start,
            end,
            normalizedLimit,
            LokiQueryDirection.Forward,
            cancellationToken);

        nodeLogs = MergeLogRecords(
            nodeLogs,
            await _lokiQueryClient.QueryRangeAsync(
                BuildLegacyProcessStepLogQuery(runtimeProcessStepId),
                start,
                end,
                normalizedLimit,
                LokiQueryDirection.Forward,
                cancellationToken));

        if (nodeLogs.Count == 0)
        {
            nodeLogs = await _lokiQueryClient.QueryRangeAsync(
                BuildProcessStepLogFallbackQuery(runtimeProcessStepId),
                start,
                end,
                normalizedLimit,
                LokiQueryDirection.Forward,
                cancellationToken);
        }

        var entries = nodeLogs
            .Where(record => RecordMatchesProcessStep(record, processId, runtimeProcessStepId))
            .Select(record => new NodeLogEntryViewModel
            {
                Cursor = record.Cursor,
                Timestamp = record.Timestamp,
                Message = FormatLogMessage(record.Message),
                ServiceName = record.Labels.TryGetValue("service_name", out var serviceName) ? serviceName : null,
                Level = record.Labels.TryGetValue("detected_level", out var level) ? level : null,
            })
            .ToList();

        return new ProcessStepLogBatchViewModel
        {
            Entries = entries,
            NextCursor = entries.Count > 0 ? entries[^1].Cursor : cursor,
        };
    }

    public async Task<ProcessLogBatchViewModel> GetProcessLogsAsync(
        Guid processId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var start = ResolveLogStart(from, cursor);
        var end = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var normalizedLimit = Math.Clamp(limit, 1, 500);
        var processLogs = await _lokiQueryClient.QueryRangeAsync(
            BuildProcessLogQuery(processId),
            start,
            end,
            normalizedLimit,
            LokiQueryDirection.Forward,
            cancellationToken);

        processLogs = MergeLogRecords(
            processLogs,
            await _lokiQueryClient.QueryRangeAsync(
                BuildLegacyProcessLogQuery(processId),
                start,
                end,
                normalizedLimit,
                LokiQueryDirection.Forward,
                cancellationToken));

        if (processLogs.Count == 0)
        {
            processLogs = await _lokiQueryClient.QueryRangeAsync(
                BuildProcessLogFallbackQuery(processId),
                start,
                end,
                normalizedLimit,
                LokiQueryDirection.Forward,
                cancellationToken);
        }

        var entries = processLogs
            .Where(record => RecordMatchesProcess(record, processId))
            .Select(MapProcessLogEntry)
            .ToList();

        return new ProcessLogBatchViewModel
        {
            Entries = entries,
            NextCursor = entries.Count > 0 ? entries[^1].Cursor : cursor,
        };
    }

    public Task<JsonDocument> QueryPrometheusAsync(string expression, CancellationToken cancellationToken) =>
        _prometheusQueryClient.QueryAsync(expression, cancellationToken);

    private async Task<List<ProcessLifecycleLogEntry>> GetProcessLifecycleEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var rawLogs = await _lokiQueryClient.QueryRangeAsync(
            "{service_name=\"backend-engine\"} |= \"\\\"eventCategory\\\":\\\"process.lifecycle\\\"\"",
            from,
            to,
            SummaryLogLimit,
            LokiQueryDirection.Backward,
            cancellationToken);

        return rawLogs
            .Select(record => TryParseProcessLifecycleLogEntry(record.Message, record.Timestamp))
            .Where(entry => entry is not null)
            .Cast<ProcessLifecycleLogEntry>()
            .ToList();
    }

    private async Task<List<ProcessStepLifecycleLogEntry>> GetStepLifecycleEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var rawLogs = await _lokiQueryClient.QueryRangeAsync(
            "{service_name=\"backend-engine\"} |= \"\\\"eventCategory\\\":\\\"step.lifecycle\\\"\"",
            from,
            to,
            SummaryLogLimit,
            LokiQueryDirection.Backward,
            cancellationToken);

        return rawLogs
            .Select(record => TryParseProcessStepLifecycleLogEntry(record.Message, record.Timestamp))
            .Where(entry => entry is not null)
            .Cast<ProcessStepLifecycleLogEntry>()
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

    private static string BuildProcessStepLogQuery(Guid runtimeProcessStepId) =>
        $"{{process_step_id=\"{runtimeProcessStepId:D}\"}}";

    private static string BuildLegacyProcessStepLogQuery(Guid runtimeProcessStepId) =>
        $"{{workflow_task_instance_id=\"{runtimeProcessStepId:D}\"}}";

    private static string BuildProcessStepLogFallbackQuery(Guid runtimeProcessStepId) =>
        $"{{service_name=~\".+\"}} |= \"{runtimeProcessStepId:D}\"";

    private static string BuildProcessLogQuery(Guid processId) =>
        $"{{process_id=\"{processId:D}\"}}";

    private static string BuildLegacyProcessLogQuery(Guid processId) =>
        $"{{workflow_instance_id=\"{processId:D}\"}}";

    private static string BuildProcessLogFallbackQuery(Guid processId) =>
        $"{{service_name=~\".+\"}} |= \"{processId:D}\"";

    private static IReadOnlyList<LokiLogRecord> MergeLogRecords(
        IReadOnlyList<LokiLogRecord> first,
        IReadOnlyList<LokiLogRecord> second)
    {
        if (second.Count == 0)
        {
            return first;
        }

        if (first.Count == 0)
        {
            return second;
        }

        return first
            .Concat(second)
            .GroupBy(record => record.Cursor, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(record => record.Timestamp)
            .ToList();
    }

    private static bool RecordMatchesProcessStep(
        LokiLogRecord record,
        Guid processId,
        Guid runtimeProcessStepId)
    {
        if (TryGetGuidLabel(record.Labels, "process_step_id") == runtimeProcessStepId ||
            TryGetGuidLabel(record.Labels, "workflow_task_instance_id") == runtimeProcessStepId)
        {
            var labeledProcessId =
                TryGetGuidLabel(record.Labels, "process_id") ??
                TryGetGuidLabel(record.Labels, "workflow_instance_id");

            if (labeledProcessId is null || labeledProcessId == processId)
            {
                return true;
            }
        }

        if (record.Message.Contains(runtimeProcessStepId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return record.Message.Contains(runtimeProcessStepId.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }

    private static bool RecordMatchesProcess(
        LokiLogRecord record,
        Guid processId)
    {
        if (TryGetGuidLabel(record.Labels, "process_id") == processId ||
            TryGetGuidLabel(record.Labels, "workflow_instance_id") == processId)
        {
            return true;
        }

        if (record.Message.Contains(processId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return record.Message.Contains(processId.ToString("N"), StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessLogEntryViewModel MapProcessLogEntry(LokiLogRecord record)
    {
        var stepLifecycle = TryParseProcessStepLifecycleLogEntry(record.Message, record.Timestamp);
        if (stepLifecycle is not null)
        {
            return new ProcessLogEntryViewModel
            {
                Cursor = record.Cursor,
                Timestamp = record.Timestamp,
                Message = FormatLogMessage(record.Message),
                ProcessStepId = stepLifecycle.ProcessStepId,
                BlueprintNodeId = stepLifecycle.BlueprintNodeId,
                TaskName = stepLifecycle.StepName,
                PluginName = stepLifecycle.PluginName,
                ServiceName = record.Labels.TryGetValue("service_name", out var taskServiceName) ? taskServiceName : null,
                Level = record.Labels.TryGetValue("detected_level", out var taskLevel) ? taskLevel : null,
            };
        }

        var processLifecycle = TryParseProcessLifecycleLogEntry(record.Message, record.Timestamp);
        if (processLifecycle is not null)
        {
            return new ProcessLogEntryViewModel
            {
                Cursor = record.Cursor,
                Timestamp = record.Timestamp,
                Message = FormatLogMessage(record.Message),
                ProcessStepId = TryGetGuidLabel(record.Labels, "process_step_id"),
                BlueprintNodeId = TryGetGuidLabel(record.Labels, "blueprint.node_id"),
                TaskName = null,
                PluginName = record.Labels.TryGetValue("plugin_name", out var pluginName) ? pluginName : null,
                ServiceName = record.Labels.TryGetValue("service_name", out var processServiceName) ? processServiceName : null,
                Level = record.Labels.TryGetValue("detected_level", out var processLevel) ? processLevel : null,
            };
        }

        var fallbackPluginName = record.Labels.TryGetValue("plugin_name", out var genericPluginName)
            ? genericPluginName
            : null;

        return new ProcessLogEntryViewModel
        {
            Cursor = record.Cursor,
            Timestamp = record.Timestamp,
            Message = record.Message,
            ProcessStepId = TryGetGuidLabel(record.Labels, "process_step_id"),
            BlueprintNodeId = TryGetGuidLabel(record.Labels, "blueprint.node_id"),
            TaskName = fallbackPluginName,
            PluginName = fallbackPluginName,
            ServiceName = record.Labels.TryGetValue("service_name", out var fallbackServiceName) ? fallbackServiceName : null,
            Level = record.Labels.TryGetValue("detected_level", out var fallbackLevel) ? fallbackLevel : null,
        };
    }

    private static string FormatLogMessage(string rawMessage)
    {
        var stepLifecycle = TryParseProcessStepLifecycleLogEntry(rawMessage, timestampFallback: null);
        if (stepLifecycle is not null)
        {
            return stepLifecycle.EventName switch
            {
                "step.started" => $"Step started: {stepLifecycle.StepName} ({stepLifecycle.TaskVersion})",
                "step.completed" => $"Step completed: {stepLifecycle.StepName} ({stepLifecycle.TaskVersion}) in {stepLifecycle.DurationMs?.ToString("0")}ms",
                "step.failed" => $"Step failed: {stepLifecycle.StepName} ({stepLifecycle.TaskVersion}) - {stepLifecycle.FailureMessage}",
                _ => rawMessage,
            };
        }

        var processLifecycle = TryParseProcessLifecycleLogEntry(rawMessage, timestampFallback: null);
        if (processLifecycle is not null)
        {
            return processLifecycle.EventName switch
            {
                "process.started" => $"Process started: {processLifecycle.BlueprintName}",
                "process.completed" => $"Process completed: {processLifecycle.BlueprintName}",
                "process.failed" => $"Process failed: {processLifecycle.BlueprintName} - {processLifecycle.FailureMessage}",
                _ => rawMessage,
            };
        }

        return rawMessage;
    }

    private static ProcessLifecycleLogEntry? TryParseProcessLifecycleLogEntry(
        string rawMessage,
        DateTimeOffset? timestampFallback)
    {
        try
        {
            using var document = JsonDocument.Parse(rawMessage);
            var root = document.RootElement;
            if (!TryGetString(root, "eventCategory", out var eventCategory) ||
                !string.Equals(eventCategory, "process.lifecycle", StringComparison.Ordinal))
            {
                return null;
            }

            return new ProcessLifecycleLogEntry
            {
                EventCategory = eventCategory,
                EventName = GetRequiredString(root, "eventName"),
                Timestamp = TryGetDateTimeOffset(root, "timestamp", out var timestamp)
                    ? timestamp
                    : timestampFallback ?? DateTimeOffset.UtcNow,
                ProcessId = GetRequiredGuid(root, "processId"),
                BlueprintDefinitionId = GetRequiredInt32(root, "blueprintDefinitionId"),
                BlueprintName = GetRequiredString(root, "blueprintName", "blueprintVersionName"),
                BlueprintVersionId = GetRequiredInt32(root, "blueprintVersionId"),
                BlueprintVersionNumber = GetRequiredInt32(root, "blueprintVersionNumber"),
                Status = GetRequiredString(root, "status"),
                ProcessOutcome = GetOptionalString(root, "processOutcome"),
                FailureMessage = GetOptionalString(root, "failureMessage"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static ProcessStepLifecycleLogEntry? TryParseProcessStepLifecycleLogEntry(
        string rawMessage,
        DateTimeOffset? timestampFallback)
    {
        try
        {
            using var document = JsonDocument.Parse(rawMessage);
            var root = document.RootElement;
            if (!TryGetString(root, "eventCategory", out var eventCategory) ||
                !string.Equals(eventCategory, "step.lifecycle", StringComparison.Ordinal))
            {
                return null;
            }

            return new ProcessStepLifecycleLogEntry
            {
                EventCategory = eventCategory,
                EventName = GetRequiredString(root, "eventName"),
                Timestamp = TryGetDateTimeOffset(root, "timestamp", out var timestamp)
                    ? timestamp
                    : timestampFallback ?? DateTimeOffset.UtcNow,
                ProcessId = GetRequiredGuid(root, "processId"),
                ProcessStepId = GetRequiredGuid(root, "processStepId"),
                BlueprintNodeId = GetRequiredGuid(root, "blueprintNodeId"),
                BlueprintDefinitionId = GetRequiredInt32(root, "blueprintDefinitionId"),
                BlueprintName = GetRequiredString(root, "blueprintName", "blueprintVersionName"),
                BlueprintVersionId = GetRequiredInt32(root, "blueprintVersionId"),
                BlueprintVersionNumber = GetRequiredInt32(root, "blueprintVersionNumber"),
                StepName = GetRequiredString(root, "stepName"),
                PluginName = GetRequiredString(root, "pluginName"),
                TaskDefinitionId = GetRequiredInt32(root, "taskDefinitionId"),
                TaskVersionId = GetRequiredInt32(root, "taskVersionId"),
                TaskVersion = GetRequiredString(root, "taskVersion"),
                Status = GetRequiredString(root, "status"),
                ProcessOutcome = GetOptionalString(root, "processOutcome"),
                DurationMs = TryGetDouble(root, "durationMs", out var durationMs) ? durationMs : null,
                FailureMessage = GetOptionalString(root, "failureMessage"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string GetRequiredString(
        JsonElement root,
        string propertyName,
        string? fallbackPropertyName = null)
    {
        if (root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? throw new InvalidOperationException($"Property '{propertyName}' is null.");
        }

        if (!string.IsNullOrWhiteSpace(fallbackPropertyName) &&
            root.TryGetProperty(fallbackPropertyName, out var fallbackProperty) &&
            fallbackProperty.ValueKind == JsonValueKind.String)
        {
            return fallbackProperty.GetString()
                ?? throw new InvalidOperationException($"Property '{fallbackPropertyName}' is null.");
        }

        throw new InvalidOperationException($"Property '{propertyName}' was not present.");
    }

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
