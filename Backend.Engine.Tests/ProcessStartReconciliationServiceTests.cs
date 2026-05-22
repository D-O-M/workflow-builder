using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Repositories;
using Backend.Services.ProcessStepQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ProcessStartReconciliationServiceTests
{
    [Test]
    public async Task Pending_process_without_initial_step_is_repaired_from_start_snapshot()
    {
        var processId = Guid.NewGuid();
        var initialNodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(initialNodeId);
        var process = CreateProcess(processId, blueprint, ProcessStatus.Pending);
        var processRepository = new InMemoryProcessInstanceRepository(process);
        var stepRepository = new InMemoryProcessStepRepository();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(processRepository, stepRepository, blueprint, queueWriter);

        var result = await service.ReconcileOnceAsync(CreateOptions(), CancellationToken.None);

        var expectedStepId = ProcessStepIdentity.Create(processId, initialNodeId);
        result.MissingInitialStepsFound.ShouldBe(1);
        result.InitialStepsRepaired.ShouldBe(1);
        result.DispatchIntentsRepaired.ShouldBe(1);
        result.Failures.ShouldBe(0);
        stepRepository.States.ShouldContainKey(expectedStepId);
        var repairedStep = stepRepository.States[expectedStepId];
        repairedStep.InputHydrationPlan.ShouldNotBeNull();
        repairedStep.InputHydrationPlan!.InitialInput["town"].GetString().ShouldBe("input");
        repairedStep.Dispatch.ShouldNotBeNull();
        repairedStep.Dispatch!.ActiveIntentId.ShouldNotBeNull();
        queueWriter.Messages.Count.ShouldBe(1);
        queueWriter.Messages[0].ProcessStepId.ShouldBe(expectedStepId);
    }

    [Test]
    public async Task Disabled_dispatch_does_not_recreate_missing_initial_step()
    {
        var processId = Guid.NewGuid();
        var initialNodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(initialNodeId);
        var process = CreateProcess(processId, blueprint, ProcessStatus.Pending);
        var processRepository = new InMemoryProcessInstanceRepository(process);
        var stepRepository = new InMemoryProcessStepRepository();
        var service = CreateService(
            processRepository,
            stepRepository,
            blueprint,
            new ProcessStepQueueWriterSpy(),
            dispatchEnabled: false);

        var result = await service.ReconcileOnceAsync(CreateOptions(), CancellationToken.None);

        result.InitialStepsRepaired.ShouldBe(0);
        result.DispatchIntentsRepaired.ShouldBe(0);
        result.Failures.ShouldBe(1);
        stepRepository.States.Count.ShouldBe(0);
    }

    [Test]
    public async Task Pending_initial_step_without_dispatch_intent_is_repaired()
    {
        var processId = Guid.NewGuid();
        var initialNodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(initialNodeId);
        var process = CreateProcess(processId, blueprint, ProcessStatus.Pending);
        var processRepository = new InMemoryProcessInstanceRepository(process);
        var stepRepository = new InMemoryProcessStepRepository();
        var initialStepId = ProcessStepIdentity.Create(processId, initialNodeId);
        stepRepository.States[initialStepId] = CreateInitialStep(process, blueprint, initialStepId, dispatch: null);
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = CreateService(
            processRepository,
            stepRepository,
            blueprint,
            queueWriter,
            summaries: [CreateSummary(process, ProcessStatus.Pending)]);

        var result = await service.ReconcileOnceAsync(CreateOptions(), CancellationToken.None);

        result.MissingInitialStepsFound.ShouldBe(0);
        result.InitialStepsRepaired.ShouldBe(0);
        result.DispatchIntentsRepaired.ShouldBe(1);
        result.Failures.ShouldBe(0);
        var repairedDispatch = stepRepository.States[initialStepId].Dispatch;
        repairedDispatch.ShouldNotBeNull();
        repairedDispatch!.ActiveIntentId.ShouldNotBeNull();
        queueWriter.Messages.Count.ShouldBe(1);
    }

    [Test]
    public async Task Completed_process_is_ignored_even_if_summary_is_stale()
    {
        var processId = Guid.NewGuid();
        var initialNodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(initialNodeId);
        var process = CreateProcess(processId, blueprint, ProcessStatus.Completed);
        var stepRepository = new InMemoryProcessStepRepository();
        var service = CreateService(
            new InMemoryProcessInstanceRepository(process),
            stepRepository,
            blueprint,
            new ProcessStepQueueWriterSpy(),
            summaries: [CreateSummary(process, ProcessStatus.Pending)]);

        var result = await service.ReconcileOnceAsync(CreateOptions(), CancellationToken.None);

        result.CandidatesScanned.ShouldBe(1);
        result.InitialStepsRepaired.ShouldBe(0);
        result.DispatchIntentsRepaired.ShouldBe(0);
        result.Failures.ShouldBe(0);
        stepRepository.States.Count.ShouldBe(0);
    }

    [Test]
    public async Task Failed_process_is_ignored_even_if_summary_is_stale()
    {
        var processId = Guid.NewGuid();
        var initialNodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(initialNodeId);
        var process = CreateProcess(processId, blueprint, ProcessStatus.Failed);
        var stepRepository = new InMemoryProcessStepRepository();
        var service = CreateService(
            new InMemoryProcessInstanceRepository(process),
            stepRepository,
            blueprint,
            new ProcessStepQueueWriterSpy(),
            summaries: [CreateSummary(process, ProcessStatus.InProgress)]);

        var result = await service.ReconcileOnceAsync(CreateOptions(), CancellationToken.None);

        result.CandidatesScanned.ShouldBe(1);
        result.InitialStepsRepaired.ShouldBe(0);
        result.DispatchIntentsRepaired.ShouldBe(0);
        result.Failures.ShouldBe(0);
        stepRepository.States.Count.ShouldBe(0);
    }

    [Test]
    public async Task Missing_blueprint_is_handled_without_mutating_process_steps()
    {
        var processId = Guid.NewGuid();
        var initialNodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(initialNodeId);
        var process = CreateProcess(processId, blueprint, ProcessStatus.Pending);
        var stepRepository = new InMemoryProcessStepRepository();
        var service = CreateService(
            new InMemoryProcessInstanceRepository(process),
            stepRepository,
            blueprint: null,
            new ProcessStepQueueWriterSpy(),
            summaries: [CreateSummary(process, ProcessStatus.Pending)]);

        var result = await service.ReconcileOnceAsync(CreateOptions(), CancellationToken.None);

        result.Failures.ShouldBe(1);
        result.InitialStepsRepaired.ShouldBe(0);
        stepRepository.States.Count.ShouldBe(0);
    }

    [Test]
    public async Task Missing_start_snapshot_is_handled_without_guessing_initial_input()
    {
        var processId = Guid.NewGuid();
        var initialNodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(initialNodeId);
        var process = CreateProcess(processId, blueprint, ProcessStatus.Pending, includeStart: false);
        var stepRepository = new InMemoryProcessStepRepository();
        var service = CreateService(
            new InMemoryProcessInstanceRepository(process),
            stepRepository,
            blueprint,
            new ProcessStepQueueWriterSpy(),
            summaries: [CreateSummary(process, ProcessStatus.Pending)]);

        var result = await service.ReconcileOnceAsync(CreateOptions(), CancellationToken.None);

        result.MissingInitialStepsFound.ShouldBe(1);
        result.Failures.ShouldBe(1);
        result.InitialStepsRepaired.ShouldBe(0);
        stepRepository.States.Count.ShouldBe(0);
    }

    [Test]
    public async Task InProgress_process_missing_initial_step_is_not_recreated()
    {
        var processId = Guid.NewGuid();
        var initialNodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(initialNodeId);
        var process = CreateProcess(processId, blueprint, ProcessStatus.InProgress);
        var stepRepository = new InMemoryProcessStepRepository();
        var service = CreateService(
            new InMemoryProcessInstanceRepository(process),
            stepRepository,
            blueprint,
            new ProcessStepQueueWriterSpy(),
            summaries: [CreateSummary(process, ProcessStatus.InProgress)]);

        var result = await service.ReconcileOnceAsync(CreateOptions(), CancellationToken.None);

        result.MissingInitialStepsFound.ShouldBe(1);
        result.Failures.ShouldBe(1);
        result.InitialStepsRepaired.ShouldBe(0);
        stepRepository.States.Count.ShouldBe(0);
    }

    private static ProcessStartReconciliationService CreateService(
        InMemoryProcessInstanceRepository processRepository,
        InMemoryProcessStepRepository stepRepository,
        ExecutableBlueprint? blueprint,
        ProcessStepQueueWriterSpy queueWriter,
        IReadOnlyList<ProcessSummaryProjectionRecord>? summaries = null,
        bool dispatchEnabled = true)
    {
        var dispatchService = new ProcessStepDispatchService(
            stepRepository,
            new InMemoryProcessStepDispatchIntentRepository(),
            queueWriter,
            new ProcessStepDispatchOptions
            {
                Enabled = dispatchEnabled,
                DueBucketDuration = TimeSpan.FromMilliseconds(1),
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                RetryMaxDelay = TimeSpan.FromMilliseconds(10),
                ShardCount = 4
            },
            new ProcessStepDispatchTelemetry(),
            NullLogger<ProcessStepDispatchService>.Instance);

        return new ProcessStartReconciliationService(
            processRepository,
            stepRepository,
            new ProcessIndexProjectionServiceStub(summaries ?? []),
            new BlueprintExecutionCacheStub(blueprint),
            dispatchService,
            new ProcessStartReconciliationTelemetry(),
            NullLogger<ProcessStartReconciliationService>.Instance);
    }

    private static ProcessStartReconciliationOptions CreateOptions() =>
        new()
        {
            BatchSize = 10,
            MaxProcessAgeToScanHours = 24
        };

    private static ExecutableBlueprint CreateBlueprint(Guid initialNodeId)
    {
        var node = new ExecutableBlueprintNode
        {
            Id = initialNodeId,
            Name = "Initial",
            TaskDefinitionId = 42,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedTaskVersionId = 4201,
            ResolvedVersion = new Version(1, 0, 0),
            ExecutionOrder = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = "Input",
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["town"] = "String"
                },
                OutboundBindings = []
            },
            OutputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal),
            InboundBindings = [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };

        return new ExecutableBlueprint
        {
            Id = 101,
            DefinitionId = 7,
            VersionNumber = 1,
            Name = "Reconciliation Blueprint",
            InitialNode = node,
            AllNodes = [node]
        };
    }

    private static ProcessInstanceRecord<Dictionary<string, JsonElement>> CreateProcess(
        Guid processId,
        ExecutableBlueprint blueprint,
        ProcessStatus status,
        bool includeStart = true)
    {
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var initialStepId = ProcessStepIdentity.Create(processId, blueprint.InitialNode.Id);
        var hydrationPlan = new InputHydrationPlan
        {
            InputChannelName = "Input",
            InitialInput = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["town"] = Json("input")
            }
        };

        return new ProcessInstanceRecord<Dictionary<string, JsonElement>>
        {
            id = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Status = status,
            GlobalState = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            NodeOverrides = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal),
            ExecutionProgress = new ProcessExecutionProgress
            {
                TotalNodeCount = 1,
                TotalStepCount = 1
            },
            Start = includeStart
                ? new ProcessStartState
                {
                    InitialBlueprintNodeId = blueprint.InitialNode.Id.ToString(),
                    InitialProcessStepId = initialStepId.ToString(),
                    InitialInputHydrationPlan = hydrationPlan,
                    CreatedAt = createdAt
                }
                : null
        };
    }

    private static ProcessStepRecord CreateInitialStep(
        ProcessInstanceRecord<Dictionary<string, JsonElement>> process,
        ExecutableBlueprint blueprint,
        Guid processStepId,
        ProcessStepDispatchState? dispatch) =>
        new()
        {
            Id = processStepId.ToString(),
            ProcessId = process.ProcessId,
            BlueprintId = blueprint.Id.ToString(),
            BlueprintNodeId = blueprint.InitialNode.Id.ToString(),
            TaskDefinitionId = blueprint.InitialNode.TaskDefinitionId,
            AllowedMajor = blueprint.InitialNode.AllowedMajor,
            AllowedMinor = blueprint.InitialNode.AllowedMinor,
            Status = ProcessStepStatus.Pending,
            CreatedAt = process.CreatedAt,
            UpdatedAt = process.CreatedAt,
            InputHydrationPlan = process.Start?.InitialInputHydrationPlan,
            Dispatch = dispatch
        };

    private static ProcessSummaryProjectionRecord CreateSummary(
        ProcessInstanceRecord<Dictionary<string, JsonElement>> process,
        ProcessStatus status) =>
        new()
        {
            id = $"Tenant:{process.ProcessId}",
            PartitionKey = "default:Tenant:all:202605:00",
            TenantId = "default",
            Scope = ProcessSummaryProjectionScope.Tenant,
            ScopeId = "all",
            ShardKey = 0,
            TimeBucket = "202605",
            ProcessId = process.ProcessId,
            BlueprintDefinitionId = 7,
            BlueprintVersionId = int.Parse(process.BlueprintId),
            ProcessPartitionKey = process.BlueprintId,
            Status = status,
            CreatedAt = process.CreatedAt,
            UpdatedAt = process.UpdatedAt,
            IndexedAt = DateTimeOffset.UtcNow
        };

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private sealed class InMemoryProcessInstanceRepository
        : IProcessInstanceRepository<Dictionary<string, JsonElement>>
    {
        public InMemoryProcessInstanceRepository(params ProcessInstanceRecord<Dictionary<string, JsonElement>>[] processes)
        {
            foreach (var process in processes)
            {
                Processes[Guid.Parse(process.ProcessId)] = process;
            }
        }

        public Dictionary<Guid, ProcessInstanceRecord<Dictionary<string, JsonElement>>> Processes { get; } = [];

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> CreateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateById(
            Guid processId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            if (Processes.TryGetValue(processId, out var process) &&
                process.BlueprintId == blueprintVersionId.ToString())
            {
                return Task.FromResult(process);
            }

            throw new EntityNotFoundException<ProcessInstanceRecord<Dictionary<string, JsonElement>>>(processId.ToString());
        }

        public Task<IReadOnlyList<ProcessInstanceRecord<Dictionary<string, JsonElement>>>> GetRecentProcessStatesForReconciliationAsync(
            DateTimeOffset createdFrom,
            int maxItemCount,
            IReadOnlyCollection<ProcessStatus> statuses,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProcessInstanceRecord<Dictionary<string, JsonElement>>>>(Processes.Values
                .Where(process => process.CreatedAt >= createdFrom && statuses.Contains(process.Status))
                .OrderByDescending(process => process.CreatedAt)
                .Take(maxItemCount)
                .ToList());

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

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> UpdateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryProcessStepRepository : IProcessStepRepository
    {
        public Dictionary<Guid, ProcessStepRecord> States { get; } = [];

        public Task<ProcessStepRecord> CreateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
            ProcessStepRecord state,
            CancellationToken cancellationToken)
        {
            var processStepId = Guid.Parse(state.Id);
            if (States.TryGetValue(processStepId, out var existing))
            {
                return Task.FromResult((false, existing));
            }

            States[processStepId] = state;
            return Task.FromResult((true, state));
        }

        public Task<ProcessStepRecord> GetProcessStepById(
            Guid taskInstanceId,
            Guid processId,
            CancellationToken cancellationToken)
        {
            if (States.TryGetValue(taskInstanceId, out var step) &&
                step.ProcessId == processId.ToString())
            {
                return Task.FromResult(step);
            }

            throw new EntityNotFoundException<ProcessStepRecord>(taskInstanceId.ToString());
        }

        public Task<bool> TryUpdateStepDispatchAsync(
            Guid processStepId,
            Guid processId,
            ProcessStepDispatchState dispatch,
            CancellationToken cancellationToken)
        {
            if (!States.TryGetValue(processStepId, out var step) ||
                step.ProcessId != processId.ToString() ||
                step.Status is not ProcessStepStatus.Pending)
            {
                return Task.FromResult(false);
            }

            step.Dispatch = dispatch;
            step.UpdatedAt = dispatch.UpdatedAt;
            return Task.FromResult(true);
        }

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            int taskDefinitionId,
            int allowedMajor,
            int allowedMinor,
            int resolvedTaskVersionId,
            string resolvedTaskVersion,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
            Guid processId,
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

        public Task<ProcessStepRecord> UpdateProcessStep(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessStep(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessIndexProjectionServiceStub : IProcessIndexProjectionService
    {
        private readonly IReadOnlyList<ProcessSummaryProjectionRecord> _summaries;

        public ProcessIndexProjectionServiceStub(IReadOnlyList<ProcessSummaryProjectionRecord> summaries)
        {
            _summaries = summaries;
        }

        public Task UpsertFromProcessAsync(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessDirectoryRecord?> GetDirectoryByProcessIdAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessSummaryProjectionPage> GetRecentTenantSummariesAsync(
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessSummaryProjectionPage
            {
                Items = _summaries
                    .Where(summary => (!createdFrom.HasValue || summary.CreatedAt >= createdFrom.Value) &&
                        (statuses is null || statuses.Contains(summary.Status)))
                    .OrderByDescending(summary => summary.CreatedAt)
                    .Take(maxItemCount)
                    .ToList(),
                ContinuationToken = null
            });

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

    private sealed class BlueprintExecutionCacheStub : IBlueprintExecutionCache
    {
        private readonly ExecutableBlueprint? _blueprint;

        public BlueprintExecutionCacheStub(ExecutableBlueprint? blueprint)
        {
            _blueprint = blueprint;
        }

        public Task<ExecutableBlueprint> GetExecutableBlueprintAsync(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            _blueprint is not null
                ? Task.FromResult(_blueprint)
                : throw new EntityNotFoundException<ExecutableBlueprint>(blueprintVersionId);
    }

    private sealed class ProcessStepQueueWriterSpy : IProcessStepQueueWriter
    {
        public List<ProcessStepInitialisationMessage> Messages { get; } = [];

        public Task EnqueueAsync(
            ProcessStepInitialisationMessage message,
            CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
