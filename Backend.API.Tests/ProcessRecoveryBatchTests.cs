using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Repositories;
using Shouldly;

namespace Backend.API.Tests;

public sealed partial class ProcessQueryServiceTests
{
    [Test]
    public async Task Recovery_freezes_all_matches_or_selected_rows_across_search_pages_and_preserves_held_history()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var blueprint = new BlueprintDefinition { Key = "recovery", DisplayName = "Recovery",
            Versions = [new BlueprintVersion { Version = 1 }] };
        database.Context.BlueprintDefinitions.Add(blueprint);
        await database.Context.SaveChangesAsync();
        int version = blueprint.Versions[0].Id;
        var node = new ExecutableBlueprintNode { Id = Guid.NewGuid(), Name = "Payment", ResolvedVersion = new Version(1, 0, 0),
            InputChannel = new() { Name = "In", Properties = [], OutboundBindings = [] },
            OutputChannels = [], InboundBindings = [], DesignTimeInputOverrides = [] };
        var graph = new ExecutableBlueprint { Id = version, DefinitionId = blueprint.Id, InitialNode = node, AllNodes = [node] };
        var rows = Enumerable.Range(0, 57).Select(_ => CreateProjectionSummary(Guid.NewGuid(), blueprint.Id, version,
            "2026-04-21T11:58:00Z", ProcessStatus.Failed, ProcessSummaryProjectionScope.Tenant)).ToList();
        var ids = rows.Select(row => Guid.Parse(row.ProcessId)).ToList();
        var processes = ids.ToDictionary(id => (version, id), id => new ProcessInstanceRecord
        {
            id = id.ToString(), BlueprintId = version.ToString(), Status = ProcessStatus.Failed, ETag = "original",
            CreatedAt = DateTimeOffset.Parse("2026-04-21T11:58:00Z")
        });
        var contents = ids.ToDictionary(id => id, id => (IReadOnlyList<ProcessStepRecord>)new List<ProcessStepRecord>
        {
            new() { id = Guid.NewGuid().ToString(), ProcessId = id.ToString(), BlueprintId = version.ToString(),
                BlueprintNodeId = node.Id.ToString(), Status = ProcessStepStatus.Failed }
        });
        var states = new ProcessInstanceRepositoryStub(new Dictionary<int,IReadOnlyList<ProcessStateSummary>>(), processes);
        var steps = new ProcessStepRepositoryStub(contents);
        var query = CreateService(database, states, steps, new ProcessIndexProjectionServiceStub(rows, []));
        var store = new MemoryRecoveryStore();
        var service = new ProcessRecoveryService(store, query, states, steps, new RecoveryBlueprintCache(graph));
        var exactId = Guid.Parse(contents[ids[0]].Single().Id);
        var individual = await service.PreviewAsync(new(ids[0], exactId, null, null, null), "owner", default);
        (await store.PageAsync(individual, 0, 1, default)).Single().Plan.TargetStepIds.ShouldBe([exactId]);
        var search = new ProcessSearchRequestInputModel
        {
            CreatedFrom = DateTimeOffset.Parse("2026-04-21T11:00:00Z"),
            CreatedTo = DateTimeOffset.Parse("2026-04-21T12:00:00Z"), ProcessStatuses = ["Failed"]
        };
        var all = await service.PreviewAsync(new(null, null, search, null, [ids[5]], true), "one", default);
        all.ProcessCount.ShouldBe(56);
        all.StepCount.ShouldBe(56);
        var members = await store.PageAsync(all, 0, 100, default);
        members.Select(c => c.ProcessId).Order().ShouldBe(ids.Where(id => id != ids[5]).Order());
        var two = await service.PreviewAsync(new(null, null, search, [ids[0], ids[^1]], null), "one", default);
        two.ProcessCount.ShouldBe(2);
        await Task.WhenAll(service.ConfirmAsync(two.BatchId, new(1, "incident"), "operator", default),
            service.ConfirmAsync(two.BatchId, new(1, "duplicate"), "other", default));
        store.Confirmations.Count.ShouldBe(1);
        store.Confirmations[(two.BatchId, 1)].Actor.ShouldBe("operator");

        // Model a completed worker pass where one case was held after a new route appeared.
        var cases = await store.PageAsync(two, 0, 5, default);
        var held = cases[0];
        var oldStep = contents[held.ProcessId].Single();
        var review = new ProcessStepRecord { id = Guid.NewGuid().ToString(), LogicalStepId = oldStep.Id,
            RecoveryId = two.BatchId, RecoveryRevision = 1, RecoveryAction = "Review", BlueprintNodeId = oldStep.BlueprintNodeId,
            ProcessId = oldStep.ProcessId, BlueprintId = oldStep.BlueprintId, Status = ProcessStepStatus.Failed };
        contents[held.ProcessId] = [oldStep, review];
        processes[(version, held.ProcessId)].Recovery = new ProcessRecoveryState { RecoveryId = two.BatchId,
            Revision = 1, Outcome = "Held", EffectiveAttemptIds = new() { [oldStep.Id] = review.Id } };
        processes[(version, held.ProcessId)].ETag = "after-review";
        store.Cases[held.Id] = held with { Status = "Held" };
        store.Cases[cases[1].Id] = cases[1] with { Status = "Completed" };
        await store.ReplaceAsync(store.Batches[two.BatchId] with { Status = "Completed" }, default);

        var revised = await service.ReviseAsync(two.BatchId, new(1, "Run", []), "owner", default);
        revised.Revision.ShouldBe(2);
        revised.EligibleProcessCount.ShouldBe(1);
        revised.NewWorkPolicy.ShouldBe("Run");
        var revisedCases = await store.PageAsync(revised, 0, 5, default);
        revisedCases[0].Plan.TargetStepIds.ShouldBe([Guid.Parse(review.Id)]);
        revisedCases[1].Status.ShouldBe("Completed");
        (await service.GetRevisionAsync(two.BatchId, 1, default)).Reason.ShouldBe("incident");
        await Should.ThrowAsync<RecoveryConflictException>(() =>
            service.ConfirmAsync(two.BatchId, new(1, "obsolete"), "other", default));
        processes[(version, held.ProcessId)].ETag = "recovered-elsewhere";
        var changed = await service.ReviseAsync(two.BatchId, new(2, null, []), "owner", default);
        changed.ProcessCount.ShouldBe(2);
        changed.EligibleProcessCount.ShouldBe(0);
        changed.NewWorkPolicy.ShouldBe("Run");
        await Should.ThrowAsync<RecoveryConflictException>(() =>
            service.ConfirmAsync(two.BatchId, new(3, "no remaining work"), "owner", default));
    }

    [Test]
    public void Recovery_status_and_properties_must_match_one_failed_occurrence()
    {
        var node = Guid.NewGuid();
        ProcessStepRecord Step(ProcessStepStatus status, string client) => new()
        {
            id = Guid.NewGuid().ToString(), BlueprintNodeId = node.ToString(), Status = status,
            InputData = new() { ["in"] = JsonSerializer.SerializeToElement(new { client }) }
        };
        var search = new ProcessSearchRequestInputModel { CreatedFrom = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedTo = DateTimeOffset.UtcNow, NodeFilters = [new() { BlueprintDefinitionId = 1,
                BlueprintNodeId = node, Statuses = ["Failed"] }], PropertyFilters = [new() { Scope = "NodeInput",
                BlueprintDefinitionId = 1, BlueprintNodeId = node, PropertyKey = "client", PropertyType = "String", Value = "A" }] };
        var completed = Step(ProcessStepStatus.Completed, "A");
        var other = Step(ProcessStepStatus.Failed, "B");
        ProcessQueryService.FindFailedRecoverySteps(search, 1, 2, [completed, other]).ShouldBeEmpty();
        var match = Step(ProcessStepStatus.Failed, "A");
        ProcessQueryService.FindFailedRecoverySteps(search, 1, 2, [completed, other, match]).ShouldBe([Guid.Parse(match.Id)]);
    }

    private sealed class RecoveryBlueprintCache(ExecutableBlueprint graph) : IBlueprintExecutionCache
    {
        public Task<ExecutableBlueprint> GetExecutableBlueprintAsync(int version, CancellationToken ct) => Task.FromResult(graph);
    }

    private sealed class MemoryRecoveryStore : IProcessRecoveryStore
    {
        public Dictionary<Guid, RecoveryBatch> Batches { get; } = [];
        public Dictionary<string, RecoveryCase> Cases { get; } = [];
        public Dictionary<(Guid,int), RecoveryBatch> Confirmations { get; } = [];
        public Task CreateAsync(RecoveryBatch batch, CancellationToken ct)
        { Batches.Add(batch.BatchId, batch); return Task.CompletedTask; }
        public Task StageAsync(RecoveryCase item, CancellationToken ct)
        { Cases.Add(item.Id, item); return Task.CompletedTask; }
        public Task<RecoveryBatch?> ReadAsync(Guid id, CancellationToken ct) => Task.FromResult(Batches.GetValueOrDefault(id));
        public Task<RecoveryBatch?> ReadConfirmationAsync(Guid id, int revision, CancellationToken ct) =>
            Task.FromResult(Confirmations.GetValueOrDefault((id,revision)));
        public Task<bool> ReplaceAsync(RecoveryBatch batch, CancellationToken ct)
        { Batches[batch.BatchId] = batch; return Task.FromResult(true); }
        public Task<RecoveryBatch> ConfirmAsync(RecoveryBatch batch, CancellationToken ct)
        { Confirmations.Add((batch.BatchId,batch.Revision), batch); Batches[batch.BatchId] = batch; return Task.FromResult(batch); }
        public Task<List<RecoveryCase>> PageAsync(RecoveryBatch batch, int offset, int limit, CancellationToken ct) =>
            Task.FromResult(Cases.Values.Where(c => c.Stamp == batch.Stamp).OrderBy(c => c.Ordinal).Skip(offset).Take(limit).ToList());
        public Task<List<RecoveryBatch>> RecentAsync(int offset, int limit, CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<Guid> FindForProcessAsync(Guid processId, CancellationToken ct) => throw new NotSupportedException();
    }
}
