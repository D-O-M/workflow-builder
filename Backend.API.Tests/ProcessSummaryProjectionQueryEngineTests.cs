using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Repositories.Processes;
using Shouldly;

namespace Backend.API.Tests;

public sealed class ProcessSummaryProjectionQueryEngineTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-05-22T12:00:00Z");

    [Test]
    public async Task Tenant_query_hits_expected_month_shard_partition_keys()
    {
        var reader = new FakePartitionReader([]);
        var engine = CreateEngine(reader);

        var page = await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken: null,
            maxItemCount: 10,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: DateTimeOffset.Parse("2026-05-31T23:59:59Z"),
            statuses: null,
            CancellationToken.None);

        page.Items.ShouldBeEmpty();
        page.ContinuationToken.ShouldBeNull();
        reader.Calls.Count.ShouldBe(ProcessSummaryProjectionKeys.DefaultShardCount);
        reader.Calls
            .Select(call => call.PartitionKey)
            .Order(StringComparer.Ordinal)
            .ShouldBe(Enumerable.Range(0, ProcessSummaryProjectionKeys.DefaultShardCount)
                .Select(shard => ProcessSummaryProjectionKeys.CreatePartitionKey(
                    ProcessSummaryProjectionKeys.DefaultTenantId,
                    ProcessSummaryProjectionScope.Tenant,
                    ProcessSummaryProjectionKeys.TenantScopeId,
                    "202605",
                    shard))
                .Order(StringComparer.Ordinal));
    }

    [Test]
    public async Task Blueprint_query_hits_expected_version_month_shard_partition_keys()
    {
        var reader = new FakePartitionReader([]);
        var engine = CreateEngine(reader);

        await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.BlueprintVersion,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: 7,
            blueprintVersionIds: [101, 102],
            continuationToken: null,
            maxItemCount: 10,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: DateTimeOffset.Parse("2026-05-31T23:59:59Z"),
            statuses: null,
            CancellationToken.None);

        reader.Calls.Count.ShouldBe(ProcessSummaryProjectionKeys.DefaultShardCount * 2);
        reader.Calls.Select(call => call.PartitionKey).Order(StringComparer.Ordinal).ShouldBe(
            new[] { 101, 102 }
                .SelectMany(versionId => Enumerable.Range(0, ProcessSummaryProjectionKeys.DefaultShardCount)
                    .Select(shard => ProcessSummaryProjectionKeys.CreatePartitionKey(
                        ProcessSummaryProjectionKeys.DefaultTenantId,
                        ProcessSummaryProjectionScope.BlueprintVersion,
                        versionId.ToString(),
                        "202605",
                        shard)))
                .Order(StringComparer.Ordinal));
    }

    [Test]
    public async Task Date_range_limits_bucket_enumeration()
    {
        var reader = new FakePartitionReader([]);
        var engine = CreateEngine(reader);

        await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken: null,
            maxItemCount: 10,
            createdFrom: DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
            createdTo: DateTimeOffset.Parse("2026-05-31T23:59:59Z"),
            statuses: null,
            CancellationToken.None);

        var buckets = reader.Calls
            .Select(call => call.PartitionKey.Split(':')[3])
            .Distinct()
            .Order()
            .ToArray();
        buckets.ShouldBe(["202604", "202605"]);
    }

    [Test]
    public async Task Results_are_globally_ordered_across_partitions()
    {
        var records = new[]
        {
            CreateRecord(1, ProcessSummaryProjectionScope.Tenant, 101, shard: 0, "2026-05-20T10:00:00Z"),
            CreateRecord(9, ProcessSummaryProjectionScope.Tenant, 101, shard: 1, "2026-05-20T10:00:00Z"),
            CreateRecord(2, ProcessSummaryProjectionScope.Tenant, 101, shard: 2, "2026-05-21T10:00:00Z"),
            CreateRecord(3, ProcessSummaryProjectionScope.Tenant, 101, shard: 3, "2026-05-19T10:00:00Z"),
        };
        var engine = CreateEngine(new FakePartitionReader(records));

        var page = await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken: null,
            maxItemCount: 10,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: FixedNow,
            statuses: null,
            CancellationToken.None);

        page.Items.Select(item => item.ProcessId).ShouldBe([
            ProcessId(2),
            ProcessId(9),
            ProcessId(1),
            ProcessId(3),
        ]);
    }

    [Test]
    public async Task Continuation_returns_next_page_without_duplicates()
    {
        var records = Enumerable.Range(1, 5)
            .Select(index => CreateRecord(
                index,
                ProcessSummaryProjectionScope.Tenant,
                101,
                shard: 0,
                $"2026-05-2{index}T10:00:00Z"))
            .ToArray();
        var engine = CreateEngine(new FakePartitionReader(records));

        var first = await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken: null,
            maxItemCount: 2,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: DateTimeOffset.Parse("2026-05-31T23:59:59Z"),
            statuses: null,
            CancellationToken.None);
        var second = await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken: first.ContinuationToken,
            maxItemCount: 2,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: null,
            statuses: null,
            CancellationToken.None);

        first.ContinuationToken.ShouldNotBeNull();
        second.Items.Select(item => item.ProcessId).ShouldBe([ProcessId(3), ProcessId(2)]);
        first.Items.Select(item => item.ProcessId).Intersect(second.Items.Select(item => item.ProcessId)).ShouldBeEmpty();
    }

    [Test]
    public async Task Status_filters_are_applied_inside_partition_queries()
    {
        var records = new[]
        {
            CreateRecord(1, ProcessSummaryProjectionScope.Tenant, 101, shard: 0, "2026-05-20T10:00:00Z", ProcessStatus.Pending),
            CreateRecord(2, ProcessSummaryProjectionScope.Tenant, 101, shard: 0, "2026-05-21T10:00:00Z", ProcessStatus.Completed),
        };
        var engine = CreateEngine(new FakePartitionReader(records));

        var page = await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken: null,
            maxItemCount: 10,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: FixedNow,
            statuses: [ProcessStatus.Completed],
            CancellationToken.None);

        page.Items.Single().ProcessId.ShouldBe(ProcessId(2));
    }

    [Test]
    public async Task Mismatched_continuation_token_is_rejected()
    {
        var records = Enumerable.Range(1, 3)
            .Select(index => CreateRecord(
                index,
                ProcessSummaryProjectionScope.Tenant,
                101,
                shard: 0,
                $"2026-05-2{index}T10:00:00Z"))
            .ToArray();
        var engine = CreateEngine(new FakePartitionReader(records));
        var first = await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken: null,
            maxItemCount: 1,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: FixedNow,
            statuses: null,
            CancellationToken.None);

        await Should.ThrowAsync<ArgumentException>(() => engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.Tenant,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: null,
            blueprintVersionIds: null,
            continuationToken: first.ContinuationToken,
            maxItemCount: 1,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: FixedNow,
            statuses: [ProcessStatus.Completed],
            CancellationToken.None));
    }

    [Test]
    public async Task Empty_blueprint_version_id_list_returns_empty_without_querying()
    {
        var reader = new FakePartitionReader([]);
        var engine = CreateEngine(reader);

        var page = await engine.QueryRecentAsync(
            ProcessSummaryProjectionScope.BlueprintVersion,
            ProcessSummaryProjectionKeys.DefaultTenantId,
            blueprintDefinitionId: 7,
            blueprintVersionIds: [],
            continuationToken: null,
            maxItemCount: 10,
            createdFrom: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            createdTo: FixedNow,
            statuses: null,
            CancellationToken.None);

        page.Items.ShouldBeEmpty();
        page.ContinuationToken.ShouldBeNull();
        reader.Calls.ShouldBeEmpty();
    }

    private static ProcessSummaryProjectionQueryEngine CreateEngine(FakePartitionReader reader) =>
        new(reader, () => FixedNow);

    private static ProcessSummaryProjectionRecord CreateRecord(
        int id,
        ProcessSummaryProjectionScope scope,
        int blueprintVersionId,
        int shard,
        string createdAt,
        ProcessStatus status = ProcessStatus.Pending)
    {
        var created = DateTimeOffset.Parse(createdAt);
        var scopeId = scope == ProcessSummaryProjectionScope.Tenant
            ? ProcessSummaryProjectionKeys.TenantScopeId
            : blueprintVersionId.ToString();

        return new ProcessSummaryProjectionRecord
        {
            id = $"{scope}:{ProcessId(id)}",
            PartitionKey = ProcessSummaryProjectionKeys.CreatePartitionKey(
                ProcessSummaryProjectionKeys.DefaultTenantId,
                scope,
                scopeId,
                ProcessSummaryProjectionKeys.CreateTimeBucket(created),
                shard),
            TenantId = ProcessSummaryProjectionKeys.DefaultTenantId,
            Scope = scope,
            ScopeId = scopeId,
            ShardKey = shard,
            TimeBucket = ProcessSummaryProjectionKeys.CreateTimeBucket(created),
            ProcessId = ProcessId(id),
            BlueprintDefinitionId = 7,
            BlueprintVersionId = blueprintVersionId,
            ProcessPartitionKey = blueprintVersionId.ToString(),
            Status = status,
            CreatedAt = created,
            UpdatedAt = created,
            IndexedAt = created,
        };
    }

    private static string ProcessId(int id) =>
        $"00000000-0000-0000-0000-{id.ToString("D12")}";

    private sealed class FakePartitionReader : IProcessSummaryProjectionPartitionReader
    {
        private readonly IReadOnlyList<ProcessSummaryProjectionRecord> _records;

        public FakePartitionReader(IReadOnlyList<ProcessSummaryProjectionRecord> records)
        {
            _records = records;
        }

        public List<PartitionCall> Calls { get; } = [];

        public Task<ProcessSummaryProjectionPartitionPage> QueryPartitionAsync(
            string partitionKey,
            string? continuationToken,
            int maxItemCount,
            int? blueprintDefinitionId,
            DateTimeOffset? createdFrom,
            DateTimeOffset createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken)
        {
            Calls.Add(new PartitionCall(partitionKey));
            var offset = string.IsNullOrWhiteSpace(continuationToken)
                ? 0
                : int.Parse(continuationToken);
            var filtered = _records
                .Where(record => record.PartitionKey == partitionKey)
                .Where(record => !blueprintDefinitionId.HasValue || record.BlueprintDefinitionId == blueprintDefinitionId.Value)
                .Where(record => !createdFrom.HasValue || record.CreatedAt >= createdFrom.Value)
                .Where(record => record.CreatedAt <= createdTo)
                .Where(record => statuses is not { Count: > 0 } || statuses.Contains(record.Status))
                .OrderByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.ProcessId, StringComparer.Ordinal)
                .ToList();
            var items = filtered.Skip(offset).Take(maxItemCount).ToList();
            var nextOffset = offset + items.Count;

            return Task.FromResult(new ProcessSummaryProjectionPartitionPage(
                items,
                nextOffset < filtered.Count ? nextOffset.ToString() : null));
        }
    }

    private sealed record PartitionCall(string PartitionKey);
}
