using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Backend.Repositories.Processes;

public sealed class ProcessIndexProjectionService : IProcessIndexProjectionService
{
    public const string DefaultTenantId = ProcessSummaryProjectionKeys.DefaultTenantId;
    public const int DefaultShardCount = ProcessSummaryProjectionKeys.DefaultShardCount;

    private readonly IProcessDirectoryRepository _directoryRepository;
    private readonly IProcessSummaryProjectionRepository _summaryRepository;
    private readonly ILogger<ProcessIndexProjectionService>? _logger;

    public ProcessIndexProjectionService(
        IProcessDirectoryRepository directoryRepository,
        IProcessSummaryProjectionRepository summaryRepository,
        ILogger<ProcessIndexProjectionService>? logger = null)
    {
        _directoryRepository = directoryRepository;
        _summaryRepository = summaryRepository;
        _logger = logger;
    }

    public async Task UpsertFromProcessAsync(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var directoryTask = UpsertDirectoryAsync(
            process,
            blueprintDefinitionId,
            blueprintVersionId,
            now,
            cancellationToken);
        var summaryTasks = CreateSummaryRecords(
                process,
                blueprintDefinitionId,
                blueprintVersionId,
                now)
            .Select(summary => UpsertSummaryAsync(summary, cancellationToken));

        await Task.WhenAll(summaryTasks.Append(directoryTask));
    }

    public Task<ProcessDirectoryRecord?> GetDirectoryByProcessIdAsync(
        Guid processId,
        CancellationToken cancellationToken) =>
        _directoryRepository.GetByProcessIdAsync(processId, cancellationToken);

    public Task<ProcessSummaryProjectionPage> GetRecentTenantSummariesAsync(
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken) =>
        _summaryRepository.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken,
            maxItemCount,
            createdFrom,
            createdTo,
            statuses,
            cancellationToken);

    public Task<ProcessSummaryProjectionPage> GetRecentBlueprintSummariesAsync(
        int blueprintDefinitionId,
        IReadOnlyCollection<int>? blueprintVersionIds,
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken) =>
        _summaryRepository.QueryRecentAsync(
            ProcessSummaryProjectionScope.BlueprintVersion,
            DefaultTenantId,
            blueprintDefinitionId,
            blueprintVersionIds,
            continuationToken,
            maxItemCount,
            createdFrom,
            createdTo,
            statuses,
            cancellationToken);

    public static int CalculateShard(Guid processId, int shardCount = DefaultShardCount)
        => ProcessSummaryProjectionKeys.CalculateShard(processId, shardCount);

    public static string CreateTimeBucket(DateTimeOffset createdAt) =>
        ProcessSummaryProjectionKeys.CreateTimeBucket(createdAt);

    public static string CreatePartitionKey(
        string tenantId,
        ProcessSummaryProjectionScope scope,
        string scopeId,
        string timeBucket,
        int shard) =>
        ProcessSummaryProjectionKeys.CreatePartitionKey(tenantId, scope, scopeId, timeBucket, shard);

    private static ProcessDirectoryRecord CreateDirectoryRecord(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        DateTimeOffset indexedAt)
    {
        var terminalAt = process.UpdatedAt ?? process.CreatedAt;
        return new ProcessDirectoryRecord
        {
            id = process.ProcessId,
            TenantId = DefaultTenantId,
            ProcessId = process.ProcessId,
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintVersionId = blueprintVersionId,
            ProcessPartitionKey = process.BlueprintId,
            RecoverySequence = process.Recovery?.Sequence ?? 0,
            Status = process.Status,
            CreatedAt = process.CreatedAt,
            UpdatedAt = process.UpdatedAt,
            CompletedAt = process.Status is ProcessStatus.Completed ? terminalAt : null,
            FailedAt = process.Status is ProcessStatus.Failed ? terminalAt : null,
            IndexedAt = indexedAt,
        };
    }

    private async Task UpsertDirectoryAsync(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        DateTimeOffset indexedAt,
        CancellationToken cancellationToken)
    {
        var processId = Guid.Parse(process.ProcessId);
        var incoming = CreateDirectoryRecord(process, blueprintDefinitionId, blueprintVersionId, indexedAt);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var existing = await _directoryRepository.GetByProcessIdAsync(processId, cancellationToken);
            if (await _directoryRepository.TrySaveProjectionAsync(Merge(existing, incoming), existing?.ETag, cancellationToken)) return;
        }
        throw new InvalidOperationException($"Could not update the Process directory for {processId}.");
    }

    private async Task UpsertSummaryAsync(
        ProcessSummaryProjectionRecord summary,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var existing = await _summaryRepository.GetByIdAsync(summary.Id, summary.PartitionKey, cancellationToken);
            if (await _summaryRepository.TrySaveProjectionAsync(Merge(existing, summary), existing?.ETag, cancellationToken)) return;
        }
        throw new InvalidOperationException($"Could not update Process summary {summary.Id}.");
    }

    private static IReadOnlyList<ProcessSummaryProjectionRecord> CreateSummaryRecords(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        DateTimeOffset indexedAt)
    {
        var processId = Guid.Parse(process.ProcessId);
        var shard = CalculateShard(processId);
        var timeBucket = CreateTimeBucket(process.CreatedAt);
        var terminalAt = process.UpdatedAt ?? process.CreatedAt;
        var records = new List<ProcessSummaryProjectionRecord>(2);

        records.Add(CreateSummaryRecord(
            process,
            blueprintDefinitionId,
            blueprintVersionId,
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.TenantScopeId,
            shard,
            timeBucket,
            terminalAt,
            indexedAt));

        records.Add(CreateSummaryRecord(
            process,
            blueprintDefinitionId,
            blueprintVersionId,
            ProcessSummaryProjectionScope.BlueprintVersion,
            blueprintVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            shard,
            timeBucket,
            terminalAt,
            indexedAt));

        return records;
    }

    private static ProcessSummaryProjectionRecord CreateSummaryRecord(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        ProcessSummaryProjectionScope scope,
        string scopeId,
        int shard,
        string timeBucket,
        DateTimeOffset terminalAt,
        DateTimeOffset indexedAt) =>
        new()
        {
            id = $"{scope}:{process.ProcessId}",
            PartitionKey = CreatePartitionKey(DefaultTenantId, scope, scopeId, timeBucket, shard),
            TenantId = DefaultTenantId,
            Scope = scope,
            ScopeId = scopeId,
            ShardKey = shard,
            TimeBucket = timeBucket,
            ProcessId = process.ProcessId,
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintVersionId = blueprintVersionId,
            ProcessPartitionKey = process.BlueprintId,
            RecoverySequence = process.Recovery?.Sequence ?? 0,
            Status = process.Status,
            CreatedAt = process.CreatedAt,
            UpdatedAt = process.UpdatedAt,
            CompletedAt = process.Status is ProcessStatus.Completed ? terminalAt : null,
            FailedAt = process.Status is ProcessStatus.Failed ? terminalAt : null,
            IndexedAt = indexedAt,
        };

    private ProcessDirectoryRecord Merge(
        ProcessDirectoryRecord? existing,
        ProcessDirectoryRecord incoming)
    {
        if (existing is null)
        {
            return incoming;
        }

        if (existing.BlueprintDefinitionId != incoming.BlueprintDefinitionId ||
            existing.BlueprintVersionId != incoming.BlueprintVersionId)
        {
            _logger?.LogWarning(
                "Process directory record {ProcessId} moved from blueprint {OldDefinitionId}/{OldVersionId} to {NewDefinitionId}/{NewVersionId}.",
                incoming.ProcessId,
                existing.BlueprintDefinitionId,
                existing.BlueprintVersionId,
                incoming.BlueprintDefinitionId,
                incoming.BlueprintVersionId);
        }

        var incomingUpdatedAt = incoming.UpdatedAt ?? incoming.CreatedAt;
        var existingUpdatedAt = existing.UpdatedAt ?? existing.CreatedAt;
        var latest = (incoming.RecoverySequence, incomingUpdatedAt).CompareTo((existing.RecoverySequence, existingUpdatedAt)) >= 0
            ? incoming : existing;

        return new ProcessDirectoryRecord
        {
            id = incoming.Id,
            TenantId = incoming.TenantId,
            ProcessId = incoming.ProcessId,
            BlueprintDefinitionId = incoming.BlueprintDefinitionId,
            BlueprintVersionId = incoming.BlueprintVersionId,
            ProcessPartitionKey = incoming.ProcessPartitionKey,
            Status = latest.Status,
            RecoverySequence = latest.RecoverySequence,
            CreatedAt = incoming.CreatedAt <= existing.CreatedAt ? incoming.CreatedAt : existing.CreatedAt,
            UpdatedAt = Max(existing.UpdatedAt, incoming.UpdatedAt),
            CompletedAt = latest.RecoverySequence > 0 ? latest.CompletedAt : Max(existing.CompletedAt, incoming.CompletedAt),
            FailedAt = latest.RecoverySequence > 0 ? latest.FailedAt : Max(existing.FailedAt, incoming.FailedAt),
            IndexedAt = Max(existing.IndexedAt, incoming.IndexedAt) ?? incoming.IndexedAt,
        };
    }

    private static ProcessSummaryProjectionRecord Merge(
        ProcessSummaryProjectionRecord? existing,
        ProcessSummaryProjectionRecord incoming)
    {
        if (existing is null)
        {
            return incoming;
        }

        var incomingUpdatedAt = incoming.UpdatedAt ?? incoming.CreatedAt;
        var existingUpdatedAt = existing.UpdatedAt ?? existing.CreatedAt;
        var latest = (incoming.RecoverySequence, incomingUpdatedAt).CompareTo((existing.RecoverySequence, existingUpdatedAt)) >= 0
            ? incoming : existing;

        return new ProcessSummaryProjectionRecord
        {
            id = incoming.Id,
            PartitionKey = incoming.PartitionKey,
            TenantId = incoming.TenantId,
            Scope = incoming.Scope,
            ScopeId = incoming.ScopeId,
            ShardKey = incoming.ShardKey,
            TimeBucket = incoming.TimeBucket,
            ProcessId = incoming.ProcessId,
            BlueprintDefinitionId = incoming.BlueprintDefinitionId,
            BlueprintVersionId = incoming.BlueprintVersionId,
            ProcessPartitionKey = incoming.ProcessPartitionKey,
            Status = latest.Status,
            RecoverySequence = latest.RecoverySequence,
            CreatedAt = incoming.CreatedAt <= existing.CreatedAt ? incoming.CreatedAt : existing.CreatedAt,
            UpdatedAt = Max(existing.UpdatedAt, incoming.UpdatedAt),
            CompletedAt = latest.RecoverySequence > 0 ? latest.CompletedAt : Max(existing.CompletedAt, incoming.CompletedAt),
            FailedAt = latest.RecoverySequence > 0 ? latest.FailedAt : Max(existing.FailedAt, incoming.FailedAt),
            IndexedAt = Max(existing.IndexedAt, incoming.IndexedAt) ?? incoming.IndexedAt,
        };
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left.HasValue && right.HasValue
            ? left.Value >= right.Value ? left : right
            : left ?? right;
}
