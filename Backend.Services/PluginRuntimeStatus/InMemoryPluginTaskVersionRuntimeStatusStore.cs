using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;

namespace Backend.Services.PluginRuntimeStatus;

public sealed class InMemoryPluginTaskVersionRuntimeStatusStore : IPluginTaskVersionRuntimeStatusStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, PluginTaskVersionRuntimeStatusRecord> _records = new(StringComparer.Ordinal);

    public Task UpsertAsync(
        PluginTaskVersionRuntimeStatusUpdate update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var now = update.OccurredAt ?? DateTimeOffset.UtcNow;
            var id = PluginTaskVersionRuntimeStatusIds.Create(update.TaskVersionId, update.EngineInstanceId);
            if (!_records.TryGetValue(id, out var record))
            {
                record = new PluginTaskVersionRuntimeStatusRecord
                {
                    id = id,
                    TaskVersionId = update.TaskVersionId,
                    TaskDefinitionId = update.TaskDefinitionId,
                    PluginName = update.PluginName,
                    PluginVersion = update.PluginVersion.ToString(),
                    EngineInstanceId = update.EngineInstanceId,
                    CurrentStatus = update.Status,
                    CreatedAt = now,
                    UpdatedAt = now,
                    LastSeenAt = now
                };
                _records[id] = record;
            }

            ApplyUpdate(record, update, now);
        }

        return Task.CompletedTask;
    }

    public Task RefreshLastSeenAsync(
        string engineInstanceId,
        IReadOnlyCollection<int> taskVersionIds,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            foreach (var taskVersionId in taskVersionIds.Distinct())
            {
                var id = PluginTaskVersionRuntimeStatusIds.Create(taskVersionId, engineInstanceId);
                if (!_records.TryGetValue(id, out var record))
                {
                    continue;
                }

                record.LastSeenAt = refreshedAt;
                record.UpdatedAt = refreshedAt;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PluginTaskVersionRuntimeStatusRecord>> GetFreshByTaskVersionIdsAsync(
        IReadOnlyCollection<int> taskVersionIds,
        DateTimeOffset freshAfter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var ids = taskVersionIds.ToHashSet();
            IReadOnlyList<PluginTaskVersionRuntimeStatusRecord> records =
            [
                .. _records.Values
                    .Where(record => ids.Contains(record.TaskVersionId) && record.LastSeenAt >= freshAfter)
                    .Select(Clone)
            ];
            return Task.FromResult(records);
        }
    }

    private static void ApplyUpdate(
        PluginTaskVersionRuntimeStatusRecord record,
        PluginTaskVersionRuntimeStatusUpdate update,
        DateTimeOffset now)
    {
        record.TaskDefinitionId = update.TaskDefinitionId;
        record.PluginName = update.PluginName;
        record.PluginVersion = update.PluginVersion.ToString();
        record.CurrentStatus = update.Status;
        record.Message = PluginIngestionStatusMessageSanitizer.Sanitize(update.Message);
        record.Issues =
        [
            .. update.Issues
                .Take(10)
                .Select(SanitizeIssue)
        ];
        record.UpdatedAt = now;
        record.LastSeenAt = now;

        if (update.Status == PluginTaskVersionRuntimeStatus.Runnable)
        {
            record.LoadedAt = now;
            record.UnloadedAt = null;
        }
        else if (update.Status == PluginTaskVersionRuntimeStatus.UnknownNotLoaded)
        {
            record.UnloadedAt = now;
        }
    }

    private static PluginTaskVersionRuntimeIssueRecord SanitizeIssue(
        PluginTaskVersionRuntimeIssueRecord issue) =>
        new()
        {
            CapabilityId = issue.CapabilityId,
            DisplayName = issue.DisplayName,
            Kind = issue.Kind,
            Status = issue.Status,
            Category = issue.Category,
            Message = PluginIngestionStatusMessageSanitizer.Sanitize(issue.Message),
            Remediation = PluginIngestionStatusMessageSanitizer.Sanitize(issue.Remediation)
        };

    private static PluginTaskVersionRuntimeStatusRecord Clone(
        PluginTaskVersionRuntimeStatusRecord source) =>
        new()
        {
            id = source.Id,
            ETag = source.ETag,
            TaskVersionId = source.TaskVersionId,
            TaskDefinitionId = source.TaskDefinitionId,
            PluginName = source.PluginName,
            PluginVersion = source.PluginVersion,
            EngineInstanceId = source.EngineInstanceId,
            CurrentStatus = source.CurrentStatus,
            Message = source.Message,
            Issues = [.. source.Issues],
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            LastSeenAt = source.LastSeenAt,
            LoadedAt = source.LoadedAt,
            UnloadedAt = source.UnloadedAt
        };
}
