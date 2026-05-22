using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Database.State.Objects;

namespace Backend.Repositories.Processes;

public interface IProcessSummaryProjectionPartitionReader
{
    Task<ProcessSummaryProjectionPartitionPage> QueryPartitionAsync(
        string partitionKey,
        string? continuationToken,
        int maxItemCount,
        int? blueprintDefinitionId,
        DateTimeOffset? createdFrom,
        DateTimeOffset createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken);
}

public sealed record ProcessSummaryProjectionPartitionPage(
    IReadOnlyList<ProcessSummaryProjectionRecord> Items,
    string? ContinuationToken);

public sealed class ProcessSummaryProjectionQueryEngine
{
    private const int CursorVersion = 1;
    private const int MaxPageSize = 250;
    private const int PerPartitionReadSize = 50;
    private const int MaxConcurrentPartitionReads = 8;
    private const int MaxBucketsInitializedPerRequest = 3;

    private static readonly JsonSerializerOptions CursorSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IProcessSummaryProjectionPartitionReader _partitionReader;
    private readonly Func<DateTimeOffset> _utcNow;

    public ProcessSummaryProjectionQueryEngine(
        IProcessSummaryProjectionPartitionReader partitionReader,
        Func<DateTimeOffset>? utcNow = null)
    {
        _partitionReader = partitionReader ?? throw new ArgumentNullException(nameof(partitionReader));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<ProcessSummaryProjectionPage> QueryRecentAsync(
        ProcessSummaryProjectionScope scope,
        string tenantId,
        int? blueprintDefinitionId,
        IReadOnlyCollection<int>? blueprintVersionIds,
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken)
    {
        var pageSize = Math.Clamp(maxItemCount, 1, MaxPageSize);
        var normalizedVersionIds = NormalizeBlueprintVersionIds(scope, blueprintVersionIds);
        if (scope == ProcessSummaryProjectionScope.BlueprintVersion && normalizedVersionIds.Count == 0)
        {
            return new ProcessSummaryProjectionPage
            {
                Items = [],
                ContinuationToken = null,
            };
        }

        var normalizedStatuses = NormalizeStatuses(statuses);
        var cursor = string.IsNullOrWhiteSpace(continuationToken)
            ? CreateInitialCursor(createdTo ?? _utcNow())
            : DecodeCursor(continuationToken);
        var effectiveCreatedTo = cursor.CreatedTo;
        var signature = CreateSignature(
            scope,
            tenantId,
            blueprintDefinitionId,
            normalizedVersionIds,
            createdFrom,
            effectiveCreatedTo,
            normalizedStatuses);
        if (!string.Equals(cursor.Signature, signature, StringComparison.Ordinal))
        {
            throw new ArgumentException("The process summary projection continuation token does not match the active query.", nameof(continuationToken));
        }

        if (createdFrom.HasValue && createdFrom.Value > effectiveCreatedTo)
        {
            return new ProcessSummaryProjectionPage
            {
                Items = [],
                ContinuationToken = null,
            };
        }

        using var telemetry = ProcessSummaryProjectionTelemetry.StartQuery(scope);
        var items = new List<ProcessSummaryProjectionRecord>(pageSize);
        var partitionsQueried = 0;
        var bucketsInitialized = 0;

        while (items.Count < pageSize)
        {
            RemoveExhaustedPartitions(cursor);

            if (cursor.Partitions.Count == 0)
            {
                if (bucketsInitialized >= MaxBucketsInitializedPerRequest ||
                    string.IsNullOrWhiteSpace(cursor.NextTimeBucket) ||
                    !ProcessSummaryProjectionKeys.IsAtOrAfterLowerBound(cursor.NextTimeBucket, createdFrom))
                {
                    break;
                }

                cursor.Partitions.AddRange(CreatePartitionStates(
                    scope,
                    tenantId,
                    normalizedVersionIds,
                    cursor.NextTimeBucket));
                cursor.NextTimeBucket = ProcessSummaryProjectionKeys.GetPreviousTimeBucket(
                    cursor.NextTimeBucket,
                    createdFrom);
                bucketsInitialized++;
            }

            partitionsQueried += await EnsurePartitionBuffersAsync(
                cursor,
                blueprintDefinitionId,
                createdFrom,
                effectiveCreatedTo,
                normalizedStatuses,
                cancellationToken);

            RemoveExhaustedPartitions(cursor);

            var nextPartition = cursor.Partitions
                .Where(partition => partition.Buffer.Count > 0)
                .OrderByDescending(partition => partition.Buffer[0].CreatedAt)
                .ThenByDescending(partition => partition.Buffer[0].ProcessId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (nextPartition is null)
            {
                continue;
            }

            items.Add(nextPartition.Buffer[0]);
            nextPartition.Buffer.RemoveAt(0);
        }

        RemoveExhaustedPartitions(cursor);

        ProcessSummaryProjectionTelemetry.RecordResult(scope, partitionsQueried, items.Count);
        return new ProcessSummaryProjectionPage
        {
            Items = items,
            ContinuationToken = HasMore(cursor, createdFrom) ? EncodeCursor(cursor) : null,
        };

        ProcessSummaryProjectionQueryCursor CreateInitialCursor(DateTimeOffset fixedCreatedTo)
        {
            var initialCreatedTo = fixedCreatedTo.ToUniversalTime();
            return new ProcessSummaryProjectionQueryCursor
            {
                Version = CursorVersion,
                Signature = CreateSignature(
                    scope,
                    tenantId,
                    blueprintDefinitionId,
                    normalizedVersionIds,
                    createdFrom,
                    initialCreatedTo,
                    normalizedStatuses),
                CreatedTo = initialCreatedTo,
                NextTimeBucket = ProcessSummaryProjectionKeys.CreateTimeBucket(initialCreatedTo),
            };
        }
    }

    private async Task<int> EnsurePartitionBuffersAsync(
        ProcessSummaryProjectionQueryCursor cursor,
        int? blueprintDefinitionId,
        DateTimeOffset? createdFrom,
        DateTimeOffset createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken)
    {
        var partitions = cursor.Partitions
            .Where(partition => partition.Buffer.Count == 0 && !partition.IsExhausted)
            .ToList();
        if (partitions.Count == 0)
        {
            return 0;
        }

        using var throttler = new SemaphoreSlim(MaxConcurrentPartitionReads);
        var tasks = partitions.Select(async partition =>
        {
            await throttler.WaitAsync(cancellationToken);
            try
            {
                var page = await _partitionReader.QueryPartitionAsync(
                    partition.PartitionKey,
                    partition.ContinuationToken,
                    PerPartitionReadSize,
                    blueprintDefinitionId,
                    createdFrom,
                    createdTo,
                    statuses,
                    cancellationToken);

                partition.Buffer = page.Items
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.ProcessId, StringComparer.Ordinal)
                    .ToList();
                partition.ContinuationToken = page.ContinuationToken;
                partition.IsExhausted = string.IsNullOrWhiteSpace(page.ContinuationToken);
            }
            finally
            {
                throttler.Release();
            }
        });

        await Task.WhenAll(tasks);
        return partitions.Count;
    }

    private static List<ProcessSummaryProjectionPartitionState> CreatePartitionStates(
        ProcessSummaryProjectionScope scope,
        string tenantId,
        IReadOnlyCollection<int> blueprintVersionIds,
        string timeBucket)
    {
        var scopeIds = scope == ProcessSummaryProjectionScope.Tenant
            ? [ProcessSummaryProjectionKeys.TenantScopeId]
            : blueprintVersionIds
                .Order()
                .Select(versionId => versionId.ToString(CultureInfo.InvariantCulture))
                .ToArray();
        var states = new List<ProcessSummaryProjectionPartitionState>(
            scopeIds.Length * ProcessSummaryProjectionKeys.DefaultShardCount);

        foreach (var scopeId in scopeIds)
        {
            for (var shard = 0; shard < ProcessSummaryProjectionKeys.DefaultShardCount; shard++)
            {
                states.Add(new ProcessSummaryProjectionPartitionState
                {
                    PartitionKey = ProcessSummaryProjectionKeys.CreatePartitionKey(
                        tenantId,
                        scope,
                        scopeId,
                        timeBucket,
                        shard),
                });
            }
        }

        return states;
    }

    private static bool HasMore(
        ProcessSummaryProjectionQueryCursor cursor,
        DateTimeOffset? createdFrom) =>
        cursor.Partitions.Any(partition => partition.Buffer.Count > 0 || !partition.IsExhausted) ||
        (!string.IsNullOrWhiteSpace(cursor.NextTimeBucket) &&
            ProcessSummaryProjectionKeys.IsAtOrAfterLowerBound(cursor.NextTimeBucket, createdFrom));

    private static void RemoveExhaustedPartitions(ProcessSummaryProjectionQueryCursor cursor) =>
        cursor.Partitions.RemoveAll(partition => partition.IsExhausted && partition.Buffer.Count == 0);

    private static IReadOnlyList<int> NormalizeBlueprintVersionIds(
        ProcessSummaryProjectionScope scope,
        IReadOnlyCollection<int>? blueprintVersionIds) =>
        scope == ProcessSummaryProjectionScope.BlueprintVersion
            ? (blueprintVersionIds ?? [])
                .Where(versionId => versionId > 0)
                .Distinct()
                .Order()
                .ToArray()
            : [];

    private static IReadOnlyList<ProcessStatus>? NormalizeStatuses(IReadOnlyCollection<ProcessStatus>? statuses) =>
        statuses is { Count: > 0 }
            ? statuses.Distinct().Order().ToArray()
            : null;

    private static string CreateSignature(
        ProcessSummaryProjectionScope scope,
        string tenantId,
        int? blueprintDefinitionId,
        IReadOnlyCollection<int> blueprintVersionIds,
        DateTimeOffset? createdFrom,
        DateTimeOffset createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses)
    {
        var payload = JsonSerializer.Serialize(new ProcessSummaryProjectionQuerySignature
        {
            Scope = scope,
            TenantId = tenantId,
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintVersionIds = blueprintVersionIds.ToArray(),
            CreatedFrom = createdFrom?.ToUniversalTime(),
            CreatedTo = createdTo.ToUniversalTime(),
            Statuses = statuses?.ToArray() ?? [],
        }, CursorSerializerOptions);

        return ProcessSummaryProjectionKeys.ComputeSignature(payload);
    }

    private static string EncodeCursor(ProcessSummaryProjectionQueryCursor cursor) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, CursorSerializerOptions));

    private static ProcessSummaryProjectionQueryCursor DecodeCursor(string continuationToken)
    {
        try
        {
            var payload = Convert.FromBase64String(continuationToken);
            var cursor = JsonSerializer.Deserialize<ProcessSummaryProjectionQueryCursor>(
                payload,
                CursorSerializerOptions);
            if (cursor is null || cursor.Version != CursorVersion)
            {
                throw new InvalidOperationException("The continuation token payload is unsupported.");
            }

            cursor.CreatedTo = cursor.CreatedTo.ToUniversalTime();
            return cursor;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The process summary projection continuation token is invalid.", nameof(continuationToken), exception);
        }
    }

    private sealed class ProcessSummaryProjectionQueryCursor
    {
        public int Version { get; set; }
        public string Signature { get; set; } = string.Empty;
        public DateTimeOffset CreatedTo { get; set; }
        public string? NextTimeBucket { get; set; }
        public List<ProcessSummaryProjectionPartitionState> Partitions { get; set; } = [];
    }

    private sealed class ProcessSummaryProjectionPartitionState
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string? ContinuationToken { get; set; }
        public bool IsExhausted { get; set; }
        public List<ProcessSummaryProjectionRecord> Buffer { get; set; } = [];
    }

    private sealed class ProcessSummaryProjectionQuerySignature
    {
        public ProcessSummaryProjectionScope Scope { get; init; }
        public string TenantId { get; init; } = string.Empty;
        public int? BlueprintDefinitionId { get; init; }
        public int[] BlueprintVersionIds { get; init; } = [];
        public DateTimeOffset? CreatedFrom { get; init; }
        public DateTimeOffset CreatedTo { get; init; }
        public ProcessStatus[] Statuses { get; init; } = [];
    }
}
