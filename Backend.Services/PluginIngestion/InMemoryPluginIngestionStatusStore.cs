using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;

namespace Backend.Services.PluginIngestion;

public sealed class InMemoryPluginIngestionStatusStore : IPluginIngestionStatusStore
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, PluginIngestionStatusRecord> _records = new(StringComparer.Ordinal);

    public Task AppendEventAsync(
        PluginIngestionStatusUpdate update,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var id = PluginIngestionStatusIds.FromBlobName(update.BlobName);
            var now = update.OccurredAt ?? DateTimeOffset.UtcNow;
            var record = GetOrCreateRecord(id, update, now);
            ApplyUpdate(record, update, now);
        }

        return Task.CompletedTask;
    }

    public Task<bool> IsQuarantinedAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            return Task.FromResult(
                _records.TryGetValue(PluginIngestionStatusIds.FromBlobName(blobName), out var record) &&
                record.Quarantined);
        }
    }

    public Task QuarantineAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_records.TryGetValue(id, out var record))
            {
                return Task.CompletedTask;
            }

            ApplyQuarantine(record, reason, DateTimeOffset.UtcNow);
        }

        return Task.CompletedTask;
    }

    public Task UnquarantineAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_records.TryGetValue(id, out var record))
            {
                record.Quarantined = false;
                record.QuarantineReason = null;
                record.CurrentStatus = LastNonQuarantineStatus(record);
                record.UnquarantinedAt = DateTimeOffset.UtcNow;
                record.UpdatedAt = record.UnquarantinedAt.Value;
                record.LastStatusAt = record.UnquarantinedAt.Value;
                AddEvent(record, new PluginIngestionStatusEventRecord
                {
                    Status = record.CurrentStatus,
                    Message = PluginIngestionStatusMessageSanitizer.Sanitize(reason ?? "Bundle unquarantined."),
                    TaskVersionId = record.TaskVersionId,
                    OccurredAt = record.UnquarantinedAt.Value
                });
            }
        }

        return Task.CompletedTask;
    }

    public Task<PluginIngestionStatusRecord?> GetAsync(
        string id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            return Task.FromResult(_records.TryGetValue(id, out var record) ? Clone(record) : null);
        }
    }

    public Task<PluginIngestionStatusPage> QueryAsync(
        PluginIngestionStatusQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var pageSize = Math.Clamp(query.PageSize, 1, 250);
            var skip = 0;
            if (!string.IsNullOrWhiteSpace(query.ContinuationToken) &&
                !int.TryParse(query.ContinuationToken, out skip))
            {
                throw new ArgumentException("Invalid plugin ingestion status continuation token.", nameof(query));
            }

            var filtered = _records.Values
                .Where(record => string.IsNullOrWhiteSpace(query.PluginName) ||
                    string.Equals(record.PluginName, query.PluginName, StringComparison.OrdinalIgnoreCase))
                .Where(record => query.Status is null || record.CurrentStatus == query.Status.Value)
                .Where(record => query.Quarantined is null || record.Quarantined == query.Quarantined.Value)
                .OrderByDescending(record => record.UpdatedAt)
                .ThenBy(record => record.Id, StringComparer.Ordinal)
                .Skip(skip)
                .Take(pageSize + 1)
                .Select(Clone)
                .ToList();

            var hasMore = filtered.Count > pageSize;
            if (hasMore)
            {
                filtered.RemoveAt(filtered.Count - 1);
            }

            return Task.FromResult(new PluginIngestionStatusPage(
                filtered,
                hasMore ? (skip + pageSize).ToString() : null));
        }
    }

    private PluginIngestionStatusRecord GetOrCreateRecord(
        string id,
        PluginIngestionStatusUpdate update,
        DateTimeOffset now)
    {
        if (_records.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var record = new PluginIngestionStatusRecord
        {
            id = id,
            BlobName = update.BlobName,
            PluginName = update.PluginName,
            PluginVersion = update.PluginVersion?.ToString(),
            TaskVersionId = update.TaskVersionId,
            CurrentStatus = update.Status,
            CreatedAt = now,
            UpdatedAt = now,
            LastStatusAt = now
        };

        _records[id] = record;
        return record;
    }

    private static void ApplyUpdate(
        PluginIngestionStatusRecord record,
        PluginIngestionStatusUpdate update,
        DateTimeOffset now)
    {
        record.BlobName = update.BlobName;
        record.PluginName = string.IsNullOrWhiteSpace(update.PluginName) ? record.PluginName : update.PluginName;
        record.PluginVersion = update.PluginVersion?.ToString() ?? record.PluginVersion;
        record.TaskVersionId = update.TaskVersionId ?? record.TaskVersionId;
        record.CurrentStatus = update.Status;
        record.UpdatedAt = now;
        record.LastStatusAt = now;

        if (IsFailureStatus(update.Status))
        {
            record.LastFailureCategory = update.Status;
            record.LastFailureMessage = PluginIngestionStatusMessageSanitizer.Sanitize(update.Message);
        }

        if (update.Status == PluginIngestionStatus.Registered)
        {
            record.RegisteredAt = now;
            record.LastFailureCategory = null;
            record.LastFailureMessage = null;
        }

        if (update.Status == PluginIngestionStatus.Quarantined)
        {
            ApplyQuarantine(record, update.Message, now);
            return;
        }

        AddEvent(record, new PluginIngestionStatusEventRecord
        {
            Status = update.Status,
            Message = PluginIngestionStatusMessageSanitizer.Sanitize(update.Message),
            TaskVersionId = update.TaskVersionId,
            OccurredAt = now
        });
    }

    private static void ApplyQuarantine(
        PluginIngestionStatusRecord record,
        string? reason,
        DateTimeOffset now)
    {
        record.Quarantined = true;
        record.CurrentStatus = PluginIngestionStatus.Quarantined;
        record.QuarantineReason = PluginIngestionStatusMessageSanitizer.Sanitize(reason);
        record.QuarantinedAt = now;
        record.UpdatedAt = now;
        record.LastStatusAt = now;
        AddEvent(record, new PluginIngestionStatusEventRecord
        {
            Status = PluginIngestionStatus.Quarantined,
            Message = record.QuarantineReason,
            TaskVersionId = record.TaskVersionId,
            OccurredAt = now
        });
    }

    private static void AddEvent(
        PluginIngestionStatusRecord record,
        PluginIngestionStatusEventRecord statusEvent)
    {
        record.Events.Add(statusEvent);
        if (record.Events.Count > PluginIngestionStatusRecord.MaxEventCount)
        {
            record.Events.RemoveRange(0, record.Events.Count - PluginIngestionStatusRecord.MaxEventCount);
        }
    }

    private static bool IsFailureStatus(PluginIngestionStatus status) =>
        status is PluginIngestionStatus.ManifestMissing
            or PluginIngestionStatus.ManifestInvalid
            or PluginIngestionStatus.AbiInvalid
            or PluginIngestionStatus.SchemaInvalid
            or PluginIngestionStatus.CapabilityMetadataInvalid
            or PluginIngestionStatus.ConfigurationMetadataInvalid
            or PluginIngestionStatus.RuntimeLoadFailed
            or PluginIngestionStatus.BundleNameInvalid;

    private static PluginIngestionStatus LastNonQuarantineStatus(PluginIngestionStatusRecord record) =>
        record.Events
            .LastOrDefault(statusEvent => statusEvent.Status != PluginIngestionStatus.Quarantined)
            ?.Status
        ?? PluginIngestionStatus.Detected;

    private static PluginIngestionStatusRecord Clone(PluginIngestionStatusRecord source) =>
        new()
        {
            id = source.Id,
            ETag = source.ETag,
            BlobName = source.BlobName,
            PluginName = source.PluginName,
            PluginVersion = source.PluginVersion,
            TaskVersionId = source.TaskVersionId,
            CurrentStatus = source.CurrentStatus,
            LastFailureCategory = source.LastFailureCategory,
            LastFailureMessage = source.LastFailureMessage,
            Quarantined = source.Quarantined,
            QuarantineReason = source.QuarantineReason,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            LastStatusAt = source.LastStatusAt,
            RegisteredAt = source.RegisteredAt,
            QuarantinedAt = source.QuarantinedAt,
            UnquarantinedAt = source.UnquarantinedAt,
            Events = [.. source.Events]
        };
}
