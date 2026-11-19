using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Tests.TestSupport;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Backend.Repositories.Processes;
using Shouldly;

namespace Backend.API.Tests;

public sealed class ProcessProjectionServiceTests
{
    [Test]
    public async Task GetProcessStepDetailAsync_returns_mapping_and_override_summaries()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var sourceDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Lookup",
            null,
            ("TownResult", new Dictionary<string, WorkflowPropertyKind>
            {
                ["town"] = WorkflowPropertyKind.String,
            }));
        var targetDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Recommend",
            new Dictionary<string, WorkflowPropertyKind>
            {
                ["mappedTown"] = WorkflowPropertyKind.String,
                ["mode"] = WorkflowPropertyKind.String,
            });

        var repository = new BlueprintRepository(database.Context);
        var sourceNodeId = Guid.NewGuid();
        var targetNodeId = Guid.NewGuid();
        var created = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Explorer Blueprint",
                StartingBlueprintNodeId = sourceNodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = sourceNodeId,
                        TaskDefinitionId = sourceDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 120, Y = 80 },
                        OutgoingConnections =
                        [
                            new BlueprintConnectionInputModel
                            {
                                ConnectedBlueprintNodeId = targetNodeId,
                                ChannelName = "TownResult",
                                Anchor = 0,
                                Side = "left",
                                Position = 62,
                                PropertyMappings =
                                [
                                    new BlueprintPropertyMappingInputModel
                                    {
                                        SourceProperty = "town",
                                        TargetProperty = "mappedTown",
                                    }
                                ]
                            }
                        ]
                    },
                    new BlueprintNodeInputModel
                    {
                        InstanceId = targetNodeId,
                        TaskDefinitionId = targetDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 520, Y = 80 },
                        DesignTimeInputOverrides = new Dictionary<string, JsonElement>
                        {
                            ["mode"] = JsonSerializer.SerializeToElement("design-time")
                        }
                    }
                ]
            },
            CancellationToken.None);

        var processId = Guid.NewGuid();
        var processRecord = new ProcessInstanceRecord
        {
            id = processId.ToString(),
            BlueprintId = created.Versions[0].BlueprintVersionId.ToString(),
            CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z"),
            Status = ProcessStatus.Pending,
            NodeOverrides = new Dictionary<string, Dictionary<string, JsonElement>>
            {
                [targetNodeId.ToString()] = new Dictionary<string, JsonElement>
                {
                    ["mode"] = JsonSerializer.SerializeToElement("runtime")
                }
            }
        };

        var service = new ProcessProjectionService(
            database.Context,
            new ProcessStepRepositoryStub([]),
            new ProcessInstanceRepositoryStub(created.Versions[0].BlueprintVersionId, processRecord));

        var detail = await service.GetProcessStepDetailAsync(
            created.Id,
            processId,
            targetNodeId,
            CancellationToken.None);

        detail.IncomingMappings.Count.ShouldBe(1);
        detail.IncomingMappings[0].SourceBlueprintNodeId.ShouldBe(sourceNodeId);
        detail.IncomingMappings[0].SourceName.ShouldBe("Lookup");
        detail.IncomingMappings[0].ChannelName.ShouldBe("TownResult");
        detail.IncomingMappings[0].PropertyMappings.Count.ShouldBe(1);
        detail.IncomingMappings[0].PropertyMappings[0].SourceProperty.ShouldBe("town");
        detail.IncomingMappings[0].PropertyMappings[0].TargetProperty.ShouldBe("mappedTown");

        detail.DesignTimeInputOverrides.ShouldNotBeNull();
        detail.DesignTimeInputOverrides.Value.GetProperty("mode").GetString().ShouldBe("design-time");
        detail.RuntimeOverrides.ShouldNotBeNull();
        detail.RuntimeOverrides.Value.GetProperty("mode").GetString().ShouldBe("runtime");
    }

    [Test]
    public async Task GetProcessDetailAsync_returns_phase_metadata_when_process_is_child_phase()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Phase Step",
            new Dictionary<string, WorkflowPropertyKind>());

        var repository = new BlueprintRepository(database.Context);
        var nodeId = Guid.NewGuid();
        var created = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Phase Child Blueprint",
                StartingBlueprintNodeId = nodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = nodeId,
                        TaskDefinitionId = taskDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 120, Y = 80 },
                    }
                ],
            },
            CancellationToken.None);
        var phasedProcessId = Guid.NewGuid();
        var phaseId = Guid.NewGuid();
        var processId = Guid.NewGuid();
        var processRecord = new ProcessInstanceRecord
        {
            id = processId.ToString(),
            BlueprintId = created.Versions[0].BlueprintVersionId.ToString(),
            CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
            Status = ProcessStatus.Pending,
            Phase = new ProcessPhaseMetadata
            {
                PhasedProcessId = phasedProcessId.ToString(),
                PhasedBlueprintDefinitionId = 42,
                PhasedBlueprintVersionId = 99,
                PhaseId = phaseId.ToString(),
                PhaseIndex = 1,
                PhaseName = "Receive alert",
            },
        };

        var service = new ProcessProjectionService(
            database.Context,
            new ProcessStepRepositoryStub([]),
            new ProcessInstanceRepositoryStub(created.Versions[0].BlueprintVersionId, processRecord));

        var detail = await service.GetProcessDetailAsync(
            created.Id,
            processId,
            CancellationToken.None);

        detail.Phase.ShouldNotBeNull();
        detail.Phase.PhasedProcessId.ShouldBe(phasedProcessId);
        detail.Phase.PhasedBlueprintDefinitionId.ShouldBe(42);
        detail.Phase.PhasedBlueprintVersionId.ShouldBe(99);
        detail.Phase.PhaseId.ShouldBe(phaseId);
        detail.Phase.PhaseIndex.ShouldBe(1);
        detail.Phase.PhaseName.ShouldBe("Receive alert");
    }

    [Test]
    public async Task GetProcessDetailByIdAsync_uses_process_directory_for_partition_lookup()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Indexed Step",
            new Dictionary<string, WorkflowPropertyKind>());

        var repository = new BlueprintRepository(database.Context);
        var nodeId = Guid.NewGuid();
        var created = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Indexed Process Blueprint",
                StartingBlueprintNodeId = nodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = nodeId,
                        TaskDefinitionId = taskDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 120, Y = 80 },
                    }
                ],
            },
            CancellationToken.None);

        var processId = Guid.NewGuid();
        var processRecord = new ProcessInstanceRecord
        {
            id = processId.ToString(),
            BlueprintId = created.Versions[0].BlueprintVersionId.ToString(),
            CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
            Status = ProcessStatus.Pending,
        };
        var processRepository = new ProcessInstanceRepositoryStub(created.Versions[0].BlueprintVersionId, processRecord);
        var index = new ProcessIndexProjectionServiceStub(new ProcessDirectoryRecord
        {
            id = processId.ToString(),
            TenantId = "default",
            ProcessId = processId.ToString(),
            BlueprintDefinitionId = created.Id,
            BlueprintVersionId = created.Versions[0].BlueprintVersionId,
            ProcessPartitionKey = created.Versions[0].BlueprintVersionId.ToString(),
            Status = ProcessStatus.Pending,
            CreatedAt = processRecord.CreatedAt,
            UpdatedAt = processRecord.UpdatedAt,
            IndexedAt = DateTimeOffset.Parse("2026-04-21T00:10:01Z"),
        });

        var service = new ProcessProjectionService(
            database.Context,
            new ProcessStepRepositoryStub([]),
            processRepository,
            index);

        var detail = await service.GetProcessDetailByIdAsync(processId, CancellationToken.None);

        detail.ProcessId.ShouldBe(processId);
        detail.BlueprintDefinitionId.ShouldBe(created.Id);
        processRepository.RequestedBlueprintVersionIds.ShouldBe([created.Versions[0].BlueprintVersionId]);
    }

    [Test]
    public async Task BuildRealtimeBatchAsync_uses_realtime_projections_without_loading_all_process_steps()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Realtime Step",
            new Dictionary<string, WorkflowPropertyKind>());
        var repository = new BlueprintRepository(database.Context);
        var nodeId = Guid.NewGuid();
        var created = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Realtime Blueprint",
                StartingBlueprintNodeId = nodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = nodeId,
                        TaskDefinitionId = taskDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 120, Y = 80 },
                    }
                ],
            },
            CancellationToken.None);

        var processId = Guid.NewGuid();
        var processStepId = Backend.Models.ProcessStepIdentity.Create(processId, nodeId);
        var processRecord = CreateProcessRecord(processId, created.Versions[0].BlueprintVersionId, ProcessStatus.Completed);
        var stepRepository = new ProcessStepRepositoryStub([])
        {
            ThrowOnGetProcessStepsForProcess = true
        };
        var realtimeProjection = new ProcessRealtimeProjectionServiceStub
        {
            Summary = new ProcessRealtimeSummaryProjectionRecord
            {
                id = processId.ToString(),
                ProcessId = processId.ToString(),
                BlueprintDefinitionId = created.Id,
                BlueprintVersionId = created.Versions[0].BlueprintVersionId,
                Status = ProcessStatus.Completed,
                TotalStepCount = 1,
                CompletedStepCount = 1,
                CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z")
            },
            Nodes =
            [
                new ProcessNodeRealtimeProjectionRecord
                {
                    id = nodeId.ToString(),
                    ProcessId = processId.ToString(),
                    BlueprintNodeId = nodeId.ToString(),
                    AggregateStatus = ProcessStepStatus.Completed,
                    RuntimeStepCount = 1,
                    LatestProcessStepId = processStepId.ToString(),
                    ResolvedTaskVersionId = taskDefinition.Versions.Single().Id,
                    ResolvedTaskVersion = "1.0.0",
                    StartedAt = DateTimeOffset.Parse("2026-04-21T00:11:00Z"),
                    CompletedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z"),
                    CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
                    UpdatedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z")
                }
            ]
        };

        var service = new ProcessProjectionService(
            database.Context,
            stepRepository,
            new ProcessInstanceRepositoryStub(created.Versions[0].BlueprintVersionId, processRecord),
            processRealtimeProjectionService: realtimeProjection);

        var batch = await service.BuildRealtimeBatchAsync(
            created.Versions[0].BlueprintVersionId,
            created.Id,
            processId,
            [nodeId],
            includeSummary: true,
            includeNodeUpdates: true,
            CancellationToken.None);

        batch.ShouldNotBeNull();
        batch.Summaries.Single().CompletedTaskCount.ShouldBe(1);
        batch.NodeUpdates.Single().Nodes.Single().BlueprintNodeId.ShouldBe(processStepId);
        stepRepository.GetProcessStepsForProcessCallCount.ShouldBe(0);
    }

    [Test]
    public async Task BuildRealtimeBatchAsync_falls_back_and_repairs_when_projection_is_missing()
    {
        await using var database = await SqliteBlueprintDb.CreateAsync();
        var taskDefinition = await SeedTaskDefinitionWithChannelsAsync(
            database.Context,
            "Fallback Step",
            new Dictionary<string, WorkflowPropertyKind>());
        var repository = new BlueprintRepository(database.Context);
        var nodeId = Guid.NewGuid();
        var created = await repository.CreateBlueprint(
            new BlueprintInputModel
            {
                Name = "Fallback Blueprint",
                StartingBlueprintNodeId = nodeId,
                Tasks =
                [
                    new BlueprintNodeInputModel
                    {
                        InstanceId = nodeId,
                        TaskDefinitionId = taskDefinition.Id,
                        AllowedMajor = 1,
                        AllowedMinor = 0,
                        Position = new CoordinatesViewModel { X = 120, Y = 80 },
                    }
                ],
            },
            CancellationToken.None);

        var processId = Guid.NewGuid();
        var processStepId = Backend.Models.ProcessStepIdentity.Create(processId, nodeId);
        var processRecord = CreateProcessRecord(processId, created.Versions[0].BlueprintVersionId, ProcessStatus.Completed);
        var steps = new List<ProcessStepRecord>
        {
            new()
            {
                Id = processStepId.ToString(),
                ProcessId = processId.ToString(),
                BlueprintId = created.Versions[0].BlueprintVersionId.ToString(),
                BlueprintNodeId = nodeId.ToString(),
                TaskDefinitionId = taskDefinition.Id,
                ResolvedTaskVersionId = taskDefinition.Versions.Single().Id,
                ResolvedTaskVersion = "1.0.0",
                CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z"),
                StartedAt = DateTimeOffset.Parse("2026-04-21T00:11:00Z"),
                CompletedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z"),
                Status = ProcessStepStatus.Completed
            }
        };
        var stepRepository = new ProcessStepRepositoryStub(steps);
        var realtimeProjection = new ProcessRealtimeProjectionServiceStub();

        var service = new ProcessProjectionService(
            database.Context,
            stepRepository,
            new ProcessInstanceRepositoryStub(created.Versions[0].BlueprintVersionId, processRecord),
            processRealtimeProjectionService: realtimeProjection);

        var batch = await service.BuildRealtimeBatchAsync(
            created.Versions[0].BlueprintVersionId,
            created.Id,
            processId,
            [nodeId],
            includeSummary: true,
            includeNodeUpdates: true,
            CancellationToken.None);

        batch.ShouldNotBeNull();
        stepRepository.GetProcessStepsForProcessCallCount.ShouldBe(1);
        realtimeProjection.RepairCallCount.ShouldBe(1);
    }

    [Test]
    public async Task ProcessRealtimeProjectionService_updates_summary_and_node_idempotently()
    {
        var repository = new InMemoryProcessRealtimeProjectionRepository();
        var service = new ProcessRealtimeProjectionService(repository);
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var stepId = Backend.Models.ProcessStepIdentity.Create(processId, nodeId);
        var step = new ProcessStepRecord
        {
            Id = stepId.ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = "7",
            BlueprintNodeId = nodeId.ToString(),
            CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-21T00:11:00Z"),
            StartedAt = DateTimeOffset.Parse("2026-04-21T00:11:00Z"),
            Status = ProcessStepStatus.InProgress
        };

        await service.UpsertFromStepAsync(step, blueprintDefinitionId: 3, blueprintVersionId: 7, CancellationToken.None);
        await service.UpsertFromStepAsync(step, blueprintDefinitionId: 3, blueprintVersionId: 7, CancellationToken.None);

        var summary = await service.GetSummaryAsync(processId, CancellationToken.None);
        summary.ShouldNotBeNull();
        summary.TotalStepCount.ShouldBe(1);
        summary.InProgressStepCount.ShouldBe(1);

        var node = (await service.GetNodeProjectionsAsync(processId, [nodeId], CancellationToken.None)).Single();
        node.RuntimeStepCount.ShouldBe(1);
        node.AggregateStatus.ShouldBe(ProcessStepStatus.InProgress);
    }

    [Test]
    public async Task ProcessRealtimeProjectionService_aggregates_fan_out_runtime_steps()
    {
        var repository = new InMemoryProcessRealtimeProjectionRepository();
        var service = new ProcessRealtimeProjectionService(repository);
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();

        for (var index = 0; index < 3; index++)
        {
            await service.UpsertFromStepAsync(
                new ProcessStepRecord
                {
                    Id = Backend.Models.ProcessStepIdentity.Create(processId, nodeId, $"item-{index}").ToString(),
                    ProcessId = processId.ToString(),
                    BlueprintId = "7",
                    BlueprintNodeId = nodeId.ToString(),
                    CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z").AddSeconds(index),
                    UpdatedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z").AddSeconds(index),
                    StartedAt = DateTimeOffset.Parse("2026-04-21T00:11:00Z").AddSeconds(index),
                    CompletedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z").AddSeconds(index),
                    Status = ProcessStepStatus.Completed,
                    IterationContext = new ProcessStepIterationContext
                    {
                        SourceIteratorStepId = Guid.NewGuid().ToString(),
                        SourceIteratorBlueprintNodeId = Guid.NewGuid().ToString(),
                        ItemIndex = index,
                        ItemKey = $"item-{index}"
                    }
                },
                blueprintDefinitionId: 3,
                blueprintVersionId: 7,
                CancellationToken.None);
        }

        var summary = await service.GetSummaryAsync(processId, CancellationToken.None);
        summary.ShouldNotBeNull();
        summary.TotalStepCount.ShouldBe(3);
        summary.CompletedStepCount.ShouldBe(3);

        var node = (await service.GetNodeProjectionsAsync(processId, [nodeId], CancellationToken.None)).Single();
        node.RuntimeStepCount.ShouldBe(3);
        node.AggregateStatus.ShouldBe(ProcessStepStatus.Completed);
        node.CompletedAt.ShouldBe(DateTimeOffset.Parse("2026-04-21T00:12:02Z"));
    }

    [Test]
    public async Task Partial_branch_contributions_wait_for_canonical_completion_and_ignore_late_statuses()
    {
        var service = new ProcessRealtimeProjectionService(new InMemoryProcessRealtimeProjectionRepository());
        var processId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var process = new ProcessInstanceRecord
        {
            id = processId.ToString(), BlueprintId = "7", CreatedAt = now, Status = ProcessStatus.InProgress,
            ExecutionProgress = new() { TotalStepCount = 3 }
        };
        var step = new ProcessStepRecord
        {
            Id = Guid.NewGuid().ToString(), ProcessId = processId.ToString(), BlueprintId = "7", BlueprintNodeId = nodeId.ToString(),
            Status = ProcessStepStatus.Skipped, CreatedAt = now, UpdatedAt = now
        };
        await service.UpsertFromProcessAsync(process, 3, 7, CancellationToken.None);
        await service.UpsertFromStepAsync(step, 3, 7, CancellationToken.None);
        var partial = await service.GetSummaryAsync(processId, CancellationToken.None);
        partial!.Status.ShouldBe(ProcessStatus.InProgress);
        partial.TotalStepCount.ShouldBe(3);
        partial.SkippedStepCount.ShouldBe(1);
        process.Status = ProcessStatus.Completed;
        process.ExecutionProgress.CompletedStepIds.UnionWith(["decision", "active"]);
        process.ExecutionProgress.SkippedStepIds.Add(step.Id);
        await service.UpsertFromProcessAsync(process, 3, 7, CancellationToken.None);
        step.Status = ProcessStepStatus.Pending;
        await service.UpsertFromStepAsync(step, 3, 7, CancellationToken.None);
        process.Status = ProcessStatus.InProgress;
        process.ExecutionProgress = new() { TotalStepCount = 9 };
        await service.UpsertFromProcessAsync(process, 3, 7, CancellationToken.None);
        var settled = await service.GetSummaryAsync(processId, CancellationToken.None);
        settled!.Status.ShouldBe(ProcessStatus.Completed);
        settled.TotalStepCount.ShouldBe(3);
        settled.CompletedStepCount.ShouldBe(2);
        settled.SkippedStepCount.ShouldBe(1);
        settled.PendingStepCount.ShouldBe(0);
        var node = (await service.GetNodeProjectionsAsync(processId, [nodeId], CancellationToken.None)).Single();
        node.AggregateStatus.ShouldBe(ProcessStepStatus.Skipped);
    }

    private static async Task<TaskDefinition> SeedTaskDefinitionWithChannelsAsync(
        Backend.Database.Relational.BlueprintDbContext context,
        string name,
        Dictionary<string, WorkflowPropertyKind>? inputProperties,
        params (string ChannelName, Dictionary<string, WorkflowPropertyKind> Properties)[] outputChannels)
    {
        var taskDefinition = new TaskDefinition
        {
            Name = name,
            Versions =
            [
                new TaskVersion
                {
                    Version = new Version(1, 0, 0),
                    Path = $"/plugins/{name.ToLowerInvariant()}",
                    State = TaskVersionState.Active,
                    Channels = BuildChannels(inputProperties, outputChannels)
                }
            ]
        };

        context.TaskDefinitions.Add(taskDefinition);
        await context.SaveChangesAsync();
        return taskDefinition;
    }

    private static ProcessInstanceRecord CreateProcessRecord(
        Guid processId,
        int blueprintVersionId,
        ProcessStatus status) =>
        new()
        {
            id = processId.ToString(),
            BlueprintId = blueprintVersionId.ToString(),
            CreatedAt = DateTimeOffset.Parse("2026-04-21T00:10:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-04-21T00:12:00Z"),
            Status = status,
        };

    private sealed class ProcessInstanceRepositoryStub : IProcessInstanceRepository
    {
        private readonly int _blueprintVersionId;
        private readonly ProcessInstanceRecord _processRecord;

        public ProcessInstanceRepositoryStub(
            int blueprintVersionId,
            ProcessInstanceRecord processRecord)
        {
            _blueprintVersionId = blueprintVersionId;
            _processRecord = processRecord;
        }

        public List<int> RequestedBlueprintVersionIds { get; } = [];

        public Task<ProcessInstanceRecord> CreateProcessState(
            ProcessInstanceRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord> GetProcessStateById(
            Guid processId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            RequestedBlueprintVersionIds.Add(blueprintVersionId);
            if (processId == Guid.Parse(_processRecord.ProcessId) && blueprintVersionId == _blueprintVersionId)
            {
                return Task.FromResult(_processRecord);
            }

            throw new EntityNotFoundException<ProcessInstanceRecord>(processId.ToString());
        }

        public Task<List<ProcessInstanceRecord>> GetProcessStatesForBlueprint(
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
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
            int blueprintVersionId,
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord> UpdateProcessState(
            ProcessInstanceRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessState(
            ProcessInstanceRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessStepRepositoryStub : IProcessStepRepository
    {
        private readonly List<ProcessStepRecord> _records;

        public ProcessStepRepositoryStub(List<ProcessStepRecord> records)
        {
            _records = records;
        }

        public int GetProcessStepsForProcessCallCount { get; private set; }

        public bool ThrowOnGetProcessStepsForProcess { get; init; }

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

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(Guid processId, CancellationToken cancellationToken)
        {
            GetProcessStepsForProcessCallCount++;
            if (ThrowOnGetProcessStepsForProcess)
            {
                throw new InvalidOperationException("Full process-step load should not be used on the realtime projection hot path.");
            }

            return Task.FromResult(_records);
        }

        public Task<ProcessStepRecord> UpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            int taskDefinitionId,
            int allowedMajor,
            int allowedMinor,
            int resolvedTaskVersionId,
            string resolvedVersion,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            Dictionary<string, JsonElement> outputData,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            string? errorMessage,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessIndexProjectionServiceStub : IProcessIndexProjectionService
    {
        private readonly ProcessDirectoryRecord? _directory;

        public ProcessIndexProjectionServiceStub(ProcessDirectoryRecord? directory)
        {
            _directory = directory;
        }

        public Task UpsertFromProcessAsync(
            ProcessInstanceRecord process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessDirectoryRecord?> GetDirectoryByProcessIdAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                _directory is not null && _directory.ProcessId == processId.ToString()
                    ? _directory
                    : null);

        public Task<ProcessSummaryProjectionPage> GetRecentTenantSummariesAsync(
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessSummaryProjectionPage> GetRecentBlueprintSummariesAsync(
            int blueprintDefinitionId,
            IReadOnlyCollection<int>? blueprintVersionIds,
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessRealtimeProjectionServiceStub : IProcessRealtimeProjectionService
    {
        public ProcessRealtimeSummaryProjectionRecord? Summary { get; init; }

        public IReadOnlyList<ProcessNodeRealtimeProjectionRecord> Nodes { get; init; } = [];

        public int RepairCallCount { get; private set; }

        public Task UpsertFromProcessAsync(
            ProcessInstanceRecord process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UpsertFromStepAsync(
            ProcessStepRecord step,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task UpsertFromProcessAndStepsAsync(
            ProcessInstanceRecord process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            IReadOnlyCollection<ProcessStepRecord> steps,
            CancellationToken cancellationToken)
        {
            RepairCallCount++;
            return Task.CompletedTask;
        }

        public Task<ProcessRealtimeSummaryProjectionRecord?> GetSummaryAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Summary is not null && Summary.ProcessId == processId.ToString()
                    ? Summary
                    : null);

        public Task<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>> GetNodeProjectionsAsync(
            Guid processId,
            IReadOnlyCollection<Guid> blueprintNodeIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>>(
                Nodes
                    .Where(node =>
                        node.ProcessId == processId.ToString() &&
                        Guid.TryParse(node.BlueprintNodeId, out var parsedNodeId) &&
                        blueprintNodeIds.Contains(parsedNodeId))
                    .ToList());
    }

    private sealed class InMemoryProcessRealtimeProjectionRepository : IProcessRealtimeProjectionRepository
    {
        private readonly Dictionary<string, ProcessRealtimeSummaryProjectionRecord> _summaries = new(StringComparer.Ordinal);
        private readonly Dictionary<(string ProcessId, string NodeId), ProcessNodeRealtimeProjectionRecord> _nodes = new();

        public Task<ProcessRealtimeSummaryProjectionRecord?> GetSummaryAsync(
            Guid processId,
            CancellationToken cancellationToken)
        {
            _summaries.TryGetValue(processId.ToString(), out var summary);
            return Task.FromResult(summary);
        }

        public Task<bool> TrySaveSummaryAsync(
            ProcessRealtimeSummaryProjectionRecord record,
            bool create,
            CancellationToken cancellationToken)
        {
            _summaries[record.Id] = record;
            return Task.FromResult(true);
        }

        public Task<ProcessNodeRealtimeProjectionRecord?> GetNodeAsync(
            Guid processId,
            Guid blueprintNodeId,
            CancellationToken cancellationToken)
        {
            _nodes.TryGetValue((processId.ToString(), blueprintNodeId.ToString()), out var node);
            return Task.FromResult(node);
        }

        public Task<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>> GetNodesAsync(
            Guid processId,
            IReadOnlyCollection<Guid> blueprintNodeIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>>(
                blueprintNodeIds
                    .Select(nodeId => _nodes.TryGetValue((processId.ToString(), nodeId.ToString()), out var node) ? node : null)
                    .Where(node => node is not null)
                    .Cast<ProcessNodeRealtimeProjectionRecord>()
                    .ToList());

        public Task<(bool Created, ProcessNodeRealtimeProjectionRecord Record)> CreateNodeIfNotExistsAsync(
            ProcessNodeRealtimeProjectionRecord record,
            CancellationToken cancellationToken)
        {
            var key = (record.ProcessId, record.BlueprintNodeId);
            if (_nodes.TryGetValue(key, out var existing))
            {
                return Task.FromResult((false, existing));
            }

            _nodes[key] = record;
            return Task.FromResult((true, record));
        }

        public Task<bool> TryUpdateNodeAsync(
            ProcessNodeRealtimeProjectionRecord record,
            CancellationToken cancellationToken)
        {
            _nodes[(record.ProcessId, record.BlueprintNodeId)] = record;
            return Task.FromResult(true);
        }
    }

    private static List<Channel> BuildChannels(
        Dictionary<string, WorkflowPropertyKind>? inputProperties,
        IReadOnlyCollection<(string ChannelName, Dictionary<string, WorkflowPropertyKind> Properties)> outputChannels)
    {
        var channels = new List<Channel>();

        if (inputProperties is not null)
        {
            channels.Add(new InputChannelDefinition
            {
                Name = "Input",
                Properties = inputProperties
                    .Select(property => new ChannelProperty
                    {
                        Name = property.Key,
                        Kind = property.Value,
                        IsRequired = true,
                    })
                    .ToList()
            });
        }

        channels.AddRange(outputChannels.Select(channel => new OutputChannelDefinition
        {
            Name = channel.ChannelName,
            Properties = channel.Properties
                .Select(property => new ChannelProperty
                {
                    Name = property.Key,
                    Kind = property.Value,
                    IsRequired = true,
                })
                .ToList()
        }));

        return channels;
    }
}
