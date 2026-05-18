using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories.Processes;
using Shouldly;

namespace Backend.API.Tests;

public sealed class ProcessIndexProjectionServiceTests
{
    [Test]
    public async Task UpsertFromProcessAsync_writes_directory_and_dual_summary_rows()
    {
        var directory = new InMemoryProcessDirectoryRepository();
        var summaries = new InMemoryProcessSummaryProjectionRepository();
        var service = new ProcessIndexProjectionService(directory, summaries);
        var processId = Guid.NewGuid();
        var process = CreateProcess(processId, ProcessStatus.Pending, "2026-05-18T10:00:00Z");

        await service.UpsertFromProcessAsync(process, 7, 101, CancellationToken.None);

        var indexedDirectory = await directory.GetByProcessIdAsync(processId, CancellationToken.None);
        indexedDirectory.ShouldNotBeNull();
        indexedDirectory.BlueprintDefinitionId.ShouldBe(7);
        indexedDirectory.BlueprintVersionId.ShouldBe(101);
        indexedDirectory.ProcessPartitionKey.ShouldBe("101");

        summaries.Records.Count.ShouldBe(2);
        summaries.Records.Values.ShouldContain(record => record.Scope == ProcessSummaryProjectionScope.Tenant);
        summaries.Records.Values.ShouldContain(record => record.Scope == ProcessSummaryProjectionScope.BlueprintVersion);
        summaries.Records.Values.ShouldAllBe(record =>
            record.PartitionKey.Contains(ProcessIndexProjectionService.DefaultTenantId, StringComparison.Ordinal) &&
            record.ProcessId == processId.ToString());
    }

    [Test]
    public async Task UpsertFromProcessAsync_is_idempotent_and_does_not_regress_status()
    {
        var directory = new InMemoryProcessDirectoryRepository();
        var summaries = new InMemoryProcessSummaryProjectionRepository();
        var service = new ProcessIndexProjectionService(directory, summaries);
        var processId = Guid.NewGuid();
        var completed = CreateProcess(processId, ProcessStatus.Completed, "2026-05-18T10:00:00Z", "2026-05-18T10:05:00Z");
        var stalePending = CreateProcess(processId, ProcessStatus.Pending, "2026-05-18T10:00:00Z", "2026-05-18T10:01:00Z");

        await service.UpsertFromProcessAsync(completed, 7, 101, CancellationToken.None);
        await service.UpsertFromProcessAsync(stalePending, 7, 101, CancellationToken.None);
        await service.UpsertFromProcessAsync(completed, 7, 101, CancellationToken.None);

        var indexedDirectory = await directory.GetByProcessIdAsync(processId, CancellationToken.None);
        indexedDirectory.ShouldNotBeNull();
        indexedDirectory.Status.ShouldBe(ProcessStatus.Completed);
        indexedDirectory.UpdatedAt.ShouldBe(DateTimeOffset.Parse("2026-05-18T10:05:00Z"));
        indexedDirectory.CompletedAt.ShouldBe(DateTimeOffset.Parse("2026-05-18T10:05:00Z"));
        summaries.Records.Count.ShouldBe(2);
        summaries.Records.Values.ShouldAllBe(record => record.Status == ProcessStatus.Completed);
    }

    private static ProcessInstanceRecord<Dictionary<string, JsonElement>> CreateProcess(
        Guid processId,
        ProcessStatus status,
        string createdAt,
        string? updatedAt = null) =>
        new()
        {
            id = processId.ToString(),
            BlueprintId = "101",
            CreatedAt = DateTimeOffset.Parse(createdAt),
            UpdatedAt = updatedAt is null ? null : DateTimeOffset.Parse(updatedAt),
            Status = status,
            GlobalState = [],
        };

    private sealed class InMemoryProcessDirectoryRepository : IProcessDirectoryRepository
    {
        private readonly Dictionary<string, ProcessDirectoryRecord> _records = new(StringComparer.Ordinal);

        public Task<ProcessDirectoryRecord?> GetByProcessIdAsync(Guid processId, CancellationToken cancellationToken)
        {
            _records.TryGetValue(processId.ToString(), out var record);
            return Task.FromResult(record);
        }

        public Task<ProcessDirectoryRecord> UpsertAsync(ProcessDirectoryRecord record, CancellationToken cancellationToken)
        {
            _records[record.Id] = record;
            return Task.FromResult(record);
        }
    }

    private sealed class InMemoryProcessSummaryProjectionRepository : IProcessSummaryProjectionRepository
    {
        public Dictionary<(string PartitionKey, string Id), ProcessSummaryProjectionRecord> Records { get; } = [];

        public Task<ProcessSummaryProjectionRecord?> GetByIdAsync(
            string id,
            string partitionKey,
            CancellationToken cancellationToken)
        {
            Records.TryGetValue((partitionKey, id), out var record);
            return Task.FromResult(record);
        }

        public Task<ProcessSummaryProjectionRecord> UpsertAsync(
            ProcessSummaryProjectionRecord record,
            CancellationToken cancellationToken)
        {
            Records[(record.PartitionKey, record.Id)] = record;
            return Task.FromResult(record);
        }

        public Task<ProcessSummaryProjectionPage> QueryRecentAsync(
            ProcessSummaryProjectionScope scope,
            string tenantId,
            int? blueprintDefinitionId,
            IReadOnlyCollection<int>? blueprintVersionIds,
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
