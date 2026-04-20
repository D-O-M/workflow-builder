using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Backend.API.Services;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.ViewModels;
using Microsoft.Extensions.Caching.Memory;
using Shouldly;

namespace Backend.API.Tests;

public sealed class ProcessQueryServiceTests
{
    [Test]
    public async Task GetProcessHistoryAsync_returns_blueprint_name_and_version_number()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var definition = new BlueprintDefinition
        {
            Key = "intake",
            DisplayName = "Immutable Intake Blueprint",
            Versions =
            [
                new BlueprintVersion { Version = 2 }
            ]
        };

        database.Context.BlueprintDefinitions.Add(definition);
        await database.Context.SaveChangesAsync();

        var processId = Guid.NewGuid();
        var processRepository = new ProcessInstanceRepositoryStub(new Dictionary<int, IReadOnlyList<ProcessStateSummary>>
        {
            [definition.Versions[0].Id] =
            [
                new ProcessStateSummary
                {
                    id = processId.ToString(),
                    BlueprintId = definition.Versions[0].Id.ToString(),
                    CreatedAt = DateTimeOffset.Parse("2026-04-20T11:30:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-04-20T11:31:00Z"),
                    Status = ProcessStatus.Completed,
                }
            ]
        });

        var service = new ProcessQueryService(
            projectionService: null!,
            database.Context,
            processRepository,
            new MemoryCache(new MemoryCacheOptions()),
            observabilityQueryService: null!);

        var page = await service.GetProcessHistoryAsync(definition.Id, 25, null, CancellationToken.None);

        page.Items.Count.ShouldBe(1);
        page.Items[0].ProcessId.ShouldBe(processId);
        page.Items[0].BlueprintName.ShouldBe("Immutable Intake Blueprint");
        page.Items[0].BlueprintVersionNumber.ShouldBe(2);
    }

    private sealed class ProcessInstanceRepositoryStub : IProcessInstanceRepository<Dictionary<string, JsonElement>>
    {
        private readonly IReadOnlyDictionary<int, IReadOnlyList<ProcessStateSummary>> _pagesByBlueprintVersionId;

        public ProcessInstanceRepositoryStub(IReadOnlyDictionary<int, IReadOnlyList<ProcessStateSummary>> pagesByBlueprintVersionId)
        {
            _pagesByBlueprintVersionId = pagesByBlueprintVersionId;
        }

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> CreateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateById(
            Guid processId,
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<ProcessInstanceRecord<Dictionary<string, JsonElement>>>> GetProcessStatesForBlueprint(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummary?> GetLatestProcessStateForBlueprint(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
            int blueprintVersionId,
            string? continuationToken,
            int maxItemCount,
            CancellationToken cancellationToken)
        {
            var items = _pagesByBlueprintVersionId.TryGetValue(blueprintVersionId, out var page)
                ? page
                : [];

            return Task.FromResult(new ProcessStateSummaryPage
            {
                Items = items,
                ContinuationToken = null,
            });
        }

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> UpdateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
