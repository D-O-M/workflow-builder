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
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;
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

        var service = CreateService(database, processRepository, new ProcessStepRepositoryStub());

        var page = await service.GetProcessHistoryAsync(definition.Id, 25, null, CancellationToken.None);

        page.Items.Count.ShouldBe(1);
        page.Items[0].ProcessId.ShouldBe(processId);
        page.Items[0].BlueprintName.ShouldBe("Immutable Intake Blueprint");
        page.Items[0].BlueprintVersionNumber.ShouldBe(2);
    }

    [Test]
    public async Task SearchProcessesAsync_pages_results_without_hydrating_process_or_step_documents()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var definition = new BlueprintDefinition
        {
            Key = "catalog",
            DisplayName = "Catalog Sync",
            Versions =
            [
                new BlueprintVersion { Version = 3 }
            ]
        };

        database.Context.BlueprintDefinitions.Add(definition);
        await database.Context.SaveChangesAsync();

        var summaries = new[]
        {
            CreateSummary(definition.Versions[0].Id, "2026-04-21T11:58:00Z", ProcessStatus.Completed),
            CreateSummary(definition.Versions[0].Id, "2026-04-21T11:54:00Z", ProcessStatus.InProgress),
            CreateSummary(definition.Versions[0].Id, "2026-04-21T11:49:00Z", ProcessStatus.Pending),
        };

        var processRepository = new ProcessInstanceRepositoryStub(new Dictionary<int, IReadOnlyList<ProcessStateSummary>>
        {
            [definition.Versions[0].Id] = summaries,
        });
        var stepRepository = new ProcessStepRepositoryStub();
        var service = CreateService(database, processRepository, stepRepository);

        var firstPage = await service.SearchProcessesAsync(
            new ProcessSearchRequestInputModel
            {
                CreatedFrom = DateTimeOffset.Parse("2026-04-21T11:40:00Z"),
                CreatedTo = DateTimeOffset.Parse("2026-04-21T12:00:00Z"),
                Limit = 2,
            },
            CancellationToken.None);

        firstPage.Items.Select(item => item.ProcessId).ShouldBe([Guid.Parse(summaries[0].ProcessId), Guid.Parse(summaries[1].ProcessId)]);
        firstPage.HasMore.ShouldBeTrue();
        firstPage.NextCursor.ShouldNotBeNull();
        processRepository.ProcessStateFetchCount.ShouldBe(0);
        stepRepository.StepFetchCount.ShouldBe(0);

        var secondPage = await service.SearchProcessesAsync(
            new ProcessSearchRequestInputModel
            {
                CreatedFrom = DateTimeOffset.Parse("2026-04-21T11:40:00Z"),
                CreatedTo = DateTimeOffset.Parse("2026-04-21T12:00:00Z"),
                Limit = 2,
                Cursor = firstPage.NextCursor,
            },
            CancellationToken.None);

        secondPage.Items.Select(item => item.ProcessId).ShouldBe([Guid.Parse(summaries[2].ProcessId)]);
        secondPage.HasMore.ShouldBeFalse();
        processRepository.ProcessStateFetchCount.ShouldBe(0);
        stepRepository.StepFetchCount.ShouldBe(0);
    }

    [Test]
    public async Task SearchProcessesAsync_hydrates_process_state_only_for_global_property_filters()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var definition = new BlueprintDefinition
        {
            Key = "orders",
            DisplayName = "Order Intake",
            Versions =
            [
                new BlueprintVersion { Version = 1 }
            ]
        };

        database.Context.BlueprintDefinitions.Add(definition);
        await database.Context.SaveChangesAsync();

        var summary = CreateSummary(definition.Versions[0].Id, "2026-04-21T11:58:00Z", ProcessStatus.Completed);
        var processId = Guid.Parse(summary.ProcessId);
        var processState = new ProcessInstanceRecord<Dictionary<string, JsonElement>>
        {
            id = summary.ProcessId,
            BlueprintId = definition.Versions[0].Id.ToString(),
            CreatedAt = summary.CreatedAt,
            UpdatedAt = summary.UpdatedAt,
            Status = summary.Status,
            GlobalState = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["user_id"] = JsonSerializer.SerializeToElement("admin_882"),
            },
        };

        var processRepository = new ProcessInstanceRepositoryStub(
            new Dictionary<int, IReadOnlyList<ProcessStateSummary>>
            {
                [definition.Versions[0].Id] = [summary],
            },
            new Dictionary<(int BlueprintVersionId, Guid ProcessId), ProcessInstanceRecord<Dictionary<string, JsonElement>>>
            {
                [(definition.Versions[0].Id, processId)] = processState,
            });
        var stepRepository = new ProcessStepRepositoryStub();
        var service = CreateService(database, processRepository, stepRepository);

        var page = await service.SearchProcessesAsync(
            new ProcessSearchRequestInputModel
            {
                CreatedFrom = DateTimeOffset.Parse("2026-04-21T11:40:00Z"),
                CreatedTo = DateTimeOffset.Parse("2026-04-21T12:00:00Z"),
                Limit = 25,
                PropertyFilters =
                [
                    new ProcessSearchPropertyFilterInputModel
                    {
                        Scope = "GlobalState",
                        BlueprintDefinitionId = definition.Id,
                        PropertyKey = "user_id",
                        PropertyType = "String",
                        Value = "admin_882",
                    }
                ]
            },
            CancellationToken.None);

        page.Items.Count.ShouldBe(1);
        processRepository.ProcessStateFetchCount.ShouldBe(1);
        stepRepository.StepFetchCount.ShouldBe(0);
    }

    [Test]
    public async Task SearchProcessesAsync_hydrates_step_records_for_node_and_node_output_filters()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var nodeId = Guid.NewGuid();
        var taskDefinition = new TaskDefinition
        {
            Name = "POST /v1/auth",
            Versions = [],
        };
        var definition = new BlueprintDefinition
        {
            Key = "auth",
            DisplayName = "Auth Flow",
            Versions =
            [
                new BlueprintVersion
                {
                    Version = 1,
                    BlueprintNodes =
                    [
                        new BlueprintNode
                        {
                            Id = nodeId,
                            TaskDefinition = taskDefinition,
                            AllowedMajor = 1,
                            AllowedMinor = 0,
                            PositionX = 120,
                            PositionY = 80,
                        }
                    ]
                }
            ]
        };

        database.Context.TaskDefinitions.Add(taskDefinition);
        database.Context.BlueprintDefinitions.Add(definition);
        await database.Context.SaveChangesAsync();

        var summary = CreateSummary(definition.Versions[0].Id, "2026-04-21T11:57:00Z", ProcessStatus.Completed);
        var processId = Guid.Parse(summary.ProcessId);
        var steps = new List<ProcessStepRecord>
        {
            new()
            {
                id = Guid.NewGuid().ToString(),
                BlueprintNodeId = nodeId.ToString(),
                ProcessId = summary.ProcessId,
                BlueprintId = definition.Versions[0].Id.ToString(),
                CreatedAt = summary.CreatedAt,
                UpdatedAt = summary.UpdatedAt,
                Status = ProcessStepStatus.Completed,
                OutputData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["Success"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>
                    {
                        ["token"] = "abc-123",
                    })
                }
            }
        };

        var processRepository = new ProcessInstanceRepositoryStub(new Dictionary<int, IReadOnlyList<ProcessStateSummary>>
        {
            [definition.Versions[0].Id] = [summary],
        });
        var stepRepository = new ProcessStepRepositoryStub(
            new Dictionary<Guid, IReadOnlyList<ProcessStepRecord>>
            {
                [processId] = steps,
            });
        var service = CreateService(database, processRepository, stepRepository);

        var page = await service.SearchProcessesAsync(
            new ProcessSearchRequestInputModel
            {
                BlueprintDefinitionIds = [definition.Id],
                CreatedFrom = DateTimeOffset.Parse("2026-04-21T11:40:00Z"),
                CreatedTo = DateTimeOffset.Parse("2026-04-21T12:00:00Z"),
                Limit = 25,
                NodeFilters =
                [
                    new ProcessSearchNodeFilterInputModel
                    {
                        BlueprintDefinitionId = definition.Id,
                        BlueprintNodeId = nodeId,
                        Statuses = ["Completed"],
                    }
                ],
                PropertyFilters =
                [
                    new ProcessSearchPropertyFilterInputModel
                    {
                        Scope = "NodeOutput",
                        BlueprintDefinitionId = definition.Id,
                        BlueprintNodeId = nodeId,
                        PropertyKey = "token",
                        PropertyType = "String",
                        Value = "abc-123",
                    }
                ]
            },
            CancellationToken.None);

        page.Items.Count.ShouldBe(1);
        processRepository.ProcessStateFetchCount.ShouldBe(0);
        stepRepository.StepFetchCount.ShouldBe(1);
    }

    [Test]
    public async Task SearchProcessesAsync_rejects_cursors_for_a_different_filter_set()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var definition = new BlueprintDefinition
        {
            Key = "inventory",
            DisplayName = "Inventory Sync",
            Versions =
            [
                new BlueprintVersion { Version = 1 }
            ]
        };

        database.Context.BlueprintDefinitions.Add(definition);
        await database.Context.SaveChangesAsync();

        var summaries = new[]
        {
            CreateSummary(definition.Versions[0].Id, "2026-04-21T11:58:00Z", ProcessStatus.Completed),
            CreateSummary(definition.Versions[0].Id, "2026-04-21T11:52:00Z", ProcessStatus.Failed),
        };

        var processRepository = new ProcessInstanceRepositoryStub(new Dictionary<int, IReadOnlyList<ProcessStateSummary>>
        {
            [definition.Versions[0].Id] = summaries,
        });
        var service = CreateService(database, processRepository, new ProcessStepRepositoryStub());

        var firstPage = await service.SearchProcessesAsync(
            new ProcessSearchRequestInputModel
            {
                CreatedFrom = DateTimeOffset.Parse("2026-04-21T11:40:00Z"),
                CreatedTo = DateTimeOffset.Parse("2026-04-21T12:00:00Z"),
                Limit = 1,
            },
            CancellationToken.None);

        var exception = await Should.ThrowAsync<ArgumentException>(() => service.SearchProcessesAsync(
            new ProcessSearchRequestInputModel
            {
                CreatedFrom = DateTimeOffset.Parse("2026-04-21T11:40:00Z"),
                CreatedTo = DateTimeOffset.Parse("2026-04-21T12:00:00Z"),
                Limit = 1,
                ProcessStatuses = ["Failed"],
                Cursor = firstPage.NextCursor,
            },
            CancellationToken.None));

        exception.ParamName.ShouldBe("cursor");
    }

    private static ProcessStateSummary CreateSummary(
        int blueprintVersionId,
        string createdAt,
        ProcessStatus status) =>
        new()
        {
            id = Guid.NewGuid().ToString(),
            BlueprintId = blueprintVersionId.ToString(),
            CreatedAt = DateTimeOffset.Parse(createdAt),
            UpdatedAt = DateTimeOffset.Parse(createdAt).AddMinutes(1),
            Status = status,
        };

    private static ProcessQueryService CreateService(
        SqliteBlueprintDb database,
        ProcessInstanceRepositoryStub processRepository,
        ProcessStepRepositoryStub stepRepository) =>
        new(
            projectionService: null!,
            database.Context,
            processRepository,
            stepRepository,
            new MemoryCache(new MemoryCacheOptions()),
            observabilityQueryService: null!);

    private sealed class ProcessInstanceRepositoryStub : IProcessInstanceRepository<Dictionary<string, JsonElement>>
    {
        private readonly IReadOnlyDictionary<int, IReadOnlyList<ProcessStateSummary>> _itemsByBlueprintVersionId;
        private readonly IReadOnlyDictionary<(int BlueprintVersionId, Guid ProcessId), ProcessInstanceRecord<Dictionary<string, JsonElement>>> _processStatesByKey;

        public ProcessInstanceRepositoryStub(
            IReadOnlyDictionary<int, IReadOnlyList<ProcessStateSummary>> itemsByBlueprintVersionId,
            IReadOnlyDictionary<(int BlueprintVersionId, Guid ProcessId), ProcessInstanceRecord<Dictionary<string, JsonElement>>>? processStatesByKey = null)
        {
            _itemsByBlueprintVersionId = itemsByBlueprintVersionId;
            _processStatesByKey = processStatesByKey
                ?? new Dictionary<(int BlueprintVersionId, Guid ProcessId), ProcessInstanceRecord<Dictionary<string, JsonElement>>>();
        }

        public int ProcessStateFetchCount { get; private set; }

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> CreateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateById(
            Guid processId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            ProcessStateFetchCount++;
            if (_processStatesByKey.TryGetValue((blueprintVersionId, processId), out var processState))
            {
                return Task.FromResult(processState);
            }

            throw new EntityNotFoundException<ProcessInstanceRecord<Dictionary<string, JsonElement>>>(processId.ToString());
        }

        public Task<List<ProcessInstanceRecord<Dictionary<string, JsonElement>>>> GetProcessStatesForBlueprint(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummary?> GetLatestProcessStateForBlueprint(
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            var latest = _itemsByBlueprintVersionId.TryGetValue(blueprintVersionId, out var items)
                ? items.OrderByDescending(item => item.CreatedAt).FirstOrDefault()
                : null;
            return Task.FromResult(latest);
        }

        public Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
            int blueprintVersionId,
            string? continuationToken,
            int maxItemCount,
            CancellationToken cancellationToken) =>
            GetProcessStateSummaryPageForBlueprint(
                blueprintVersionId,
                continuationToken,
                maxItemCount,
                null,
                null,
                null,
                cancellationToken);

        public Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
            int blueprintVersionId,
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken)
        {
            var items = _itemsByBlueprintVersionId.TryGetValue(blueprintVersionId, out var storedItems)
                ? storedItems
                : [];
            var filtered = items
                .Where(item => !createdFrom.HasValue || item.CreatedAt >= createdFrom.Value)
                .Where(item => !createdTo.HasValue || item.CreatedAt <= createdTo.Value)
                .Where(item => statuses is not { Count: > 0 } || statuses.Contains(item.Status))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.ProcessId, StringComparer.Ordinal)
                .ToList();

            var offset = string.IsNullOrWhiteSpace(continuationToken)
                ? 0
                : int.Parse(continuationToken);
            var pageItems = filtered.Skip(offset).Take(maxItemCount).ToList();
            var nextOffset = offset + pageItems.Count;

            return Task.FromResult(new ProcessStateSummaryPage
            {
                Items = pageItems,
                ContinuationToken = nextOffset < filtered.Count ? nextOffset.ToString() : null,
            });
        }

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> UpdateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessStepRepositoryStub : IProcessStepRepository
    {
        private readonly IReadOnlyDictionary<Guid, IReadOnlyList<ProcessStepRecord>> _stepsByProcessId;

        public ProcessStepRepositoryStub(
            IReadOnlyDictionary<Guid, IReadOnlyList<ProcessStepRecord>>? stepsByProcessId = null)
        {
            _stepsByProcessId = stepsByProcessId
                ?? new Dictionary<Guid, IReadOnlyList<ProcessStepRecord>>();
        }

        public int StepFetchCount { get; private set; }

        public Task<ProcessStepRecord> CreateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> GetProcessStepById(
            Guid taskInstanceId,
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
            Guid processId,
            CancellationToken cancellationToken)
        {
            StepFetchCount++;
            var items = _stepsByProcessId.TryGetValue(processId, out var steps)
                ? steps.ToList()
                : [];
            return Task.FromResult(items);
        }

        public Task<ProcessStepRecord> UpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
