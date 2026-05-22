using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class PluginIngestionStatusRepository
    : CosmosRepository<PluginIngestionStatusRecord>,
      IPluginIngestionStatusStore
{
    private const int MaxWriteAttempts = 5;

    public PluginIngestionStatusRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.Id))
    {
    }

    public async Task<bool> IsQuarantinedAsync(
        string blobName,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(
            PluginIngestionStatusIds.FromBlobName(blobName),
            cancellationToken);
        return record?.Quarantined == true;
    }

    public Task<PluginIngestionStatusRecord?> GetAsync(
        string id,
        CancellationToken cancellationToken) =>
        GetAsync(id, new PartitionKey(id), cancellationToken);

    public Task AppendEventAsync(
        PluginIngestionStatusUpdate update,
        CancellationToken cancellationToken) =>
        MutateAsync(
            PluginIngestionStatusIds.FromBlobName(update.BlobName),
            update.BlobName,
            record => ApplyUpdate(record, update, update.OccurredAt ?? DateTimeOffset.UtcNow),
            cancellationToken);

    public Task QuarantineAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            blobName: null,
            record => ApplyQuarantine(record, reason, DateTimeOffset.UtcNow),
            cancellationToken);

    public Task UnquarantineAsync(
        string id,
        string? reason,
        CancellationToken cancellationToken) =>
        MutateAsync(
            id,
            blobName: null,
            record =>
            {
                var now = DateTimeOffset.UtcNow;
                record.Quarantined = false;
                record.QuarantineReason = null;
                record.CurrentStatus = LastNonQuarantineStatus(record);
                record.UnquarantinedAt = now;
                record.UpdatedAt = now;
                record.LastStatusAt = now;
                AddEvent(record, new PluginIngestionStatusEventRecord
                {
                    Status = record.CurrentStatus,
                    Message = PluginIngestionStatusMessageSanitizer.Sanitize(reason ?? "Bundle unquarantined."),
                    TaskVersionId = record.TaskVersionId,
                    OccurredAt = now
                });
            },
            cancellationToken);

    public async Task<PluginIngestionStatusPage> QueryAsync(
        PluginIngestionStatusQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, 250);
        var sql = """
            SELECT *
            FROM c
            WHERE (IS_NULL(@pluginName) OR c.pluginName = @pluginName)
              AND (IS_NULL(@status) OR c.currentStatus = @status)
              AND (IS_NULL(@quarantined) OR c.quarantined = @quarantined)
            ORDER BY c.updatedAt DESC
            """;
        var definition = new QueryDefinition(sql)
            .WithParameter("@pluginName", string.IsNullOrWhiteSpace(query.PluginName) ? null : query.PluginName)
            .WithParameter("@status", query.Status?.ToString())
            .WithParameter("@quarantined", query.Quarantined);

        var options = new QueryRequestOptions
        {
            MaxItemCount = pageSize
        };

        using var feed = Container.GetItemQueryIterator<PluginIngestionStatusRecord>(
            definition,
            query.ContinuationToken,
            options);

        if (!feed.HasMoreResults)
        {
            return new PluginIngestionStatusPage([], null);
        }

        var page = await feed.ReadNextAsync(cancellationToken);
        return new PluginIngestionStatusPage([.. page], page.ContinuationToken);
    }

    private async Task MutateAsync(
        string id,
        string? blobName,
        Action<PluginIngestionStatusRecord> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var record = await GetAsync(id, cancellationToken);
            if (record is null)
            {
                if (blobName is null)
                {
                    return;
                }

                record = CreateNewRecord(id, blobName, DateTimeOffset.UtcNow);
                mutate(record);

                try
                {
                    await CreateAsync(record, cancellationToken);
                    return;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    continue;
                }
            }

            mutate(record);
            try
            {
                await ReplaceAsync(record, cancellationToken, record.ETag);
                return;
            }
            catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            {
                continue;
            }
        }

        throw new InvalidOperationException(
            $"Plugin ingestion status record '{id}' could not be updated after {MaxWriteAttempts} attempts.");
    }

    private static PluginIngestionStatusRecord CreateNewRecord(
        string id,
        string blobName,
        DateTimeOffset now) =>
        new()
        {
            id = id,
            BlobName = blobName,
            CurrentStatus = PluginIngestionStatus.Detected,
            CreatedAt = now,
            UpdatedAt = now,
            LastStatusAt = now
        };

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
}
