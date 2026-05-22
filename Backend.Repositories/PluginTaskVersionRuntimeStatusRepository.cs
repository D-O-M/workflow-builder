using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class PluginTaskVersionRuntimeStatusRepository
    : CosmosRepository<PluginTaskVersionRuntimeStatusRecord>,
      IPluginTaskVersionRuntimeStatusStore
{
    private const int MaxWriteAttempts = 5;

    public PluginTaskVersionRuntimeStatusRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.TaskVersionId))
    {
    }

    public Task UpsertAsync(
        PluginTaskVersionRuntimeStatusUpdate update,
        CancellationToken cancellationToken) =>
        MutateAsync(update, cancellationToken);

    public async Task RefreshLastSeenAsync(
        string engineInstanceId,
        IReadOnlyCollection<int> taskVersionIds,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken)
    {
        foreach (var taskVersionId in taskVersionIds.Distinct())
        {
            var id = PluginTaskVersionRuntimeStatusIds.Create(taskVersionId, engineInstanceId);
            for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
            {
                var record = await GetAsync(id, new PartitionKey(taskVersionId), cancellationToken);
                if (record is null)
                {
                    break;
                }

                record.LastSeenAt = refreshedAt;
                record.UpdatedAt = refreshedAt;
                try
                {
                    await ReplaceAsync(record, cancellationToken, record.ETag);
                    break;
                }
                catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                {
                    continue;
                }
            }
        }
    }

    public async Task<IReadOnlyList<PluginTaskVersionRuntimeStatusRecord>> GetFreshByTaskVersionIdsAsync(
        IReadOnlyCollection<int> taskVersionIds,
        DateTimeOffset freshAfter,
        CancellationToken cancellationToken)
    {
        if (taskVersionIds.Count == 0)
        {
            return [];
        }

        var results = new List<PluginTaskVersionRuntimeStatusRecord>();
        foreach (var taskVersionId in taskVersionIds.Distinct())
        {
            var query = new QueryDefinition(
                """
                SELECT *
                FROM c
                WHERE c.taskVersionId = @taskVersionId
                  AND c.lastSeenAt >= @freshAfter
                """)
                .WithParameter("@taskVersionId", taskVersionId)
                .WithParameter("@freshAfter", freshAfter);

            var page = await QueryToListAsync(
                query,
                cancellationToken,
                new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(taskVersionId)
                });
            results.AddRange(page);
        }

        return results;
    }

    private async Task MutateAsync(
        PluginTaskVersionRuntimeStatusUpdate update,
        CancellationToken cancellationToken)
    {
        var id = PluginTaskVersionRuntimeStatusIds.Create(update.TaskVersionId, update.EngineInstanceId);
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var now = update.OccurredAt ?? DateTimeOffset.UtcNow;
            var record = await GetAsync(id, new PartitionKey(update.TaskVersionId), cancellationToken);
            if (record is null)
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
                ApplyUpdate(record, update, now);
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

            ApplyUpdate(record, update, now);
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
            $"Plugin task-version runtime status '{id}' could not be updated after {MaxWriteAttempts} attempts.");
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
}
