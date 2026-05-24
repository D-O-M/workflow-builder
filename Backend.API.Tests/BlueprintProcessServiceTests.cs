using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Plugins.Communication;
using Backend.Services.ProcessStepQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.API.Tests;

public sealed class BlueprintProcessServiceTests
{
    [Test]
    public async Task StartLatestVersionAsync_throws_for_unknown_node_override()
    {
        var initialNodeId = Guid.NewGuid();
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), CreateExecutableBlueprint(initialNodeId));
        var processSteps = new ProcessStepRepositorySpy();
        var processInstances = new ProcessInstanceRepositorySpy();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = new BlueprintProcessService(
            processSteps,
            CreateDispatchService(processSteps, queueWriter),
            new BlueprintExecutionCacheStub(CreateExecutableBlueprint(initialNodeId)),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance);

        var ex = await Should.ThrowAsync<InvalidDataException>(() => service.StartLatestVersionAsync(
            7,
            new BlueprintProcessInputModel
            {
                NodeOverrides = new Dictionary<Guid, Dictionary<string, JsonElement>>
                {
                    [Guid.NewGuid()] = new()
                    {
                        ["town"] = Json("runtime")
                    }
                }
            },
            CancellationToken.None));

        ex.Message.ShouldContain("unknown blueprint node");
        processInstances.CreatedState.ShouldBeNull();
        processSteps.CreatedStep.ShouldBeNull();
        queueWriter.Messages.Count.ShouldBe(0);
    }

    [Test]
    public async Task StartLatestVersionAsync_throws_for_unknown_node_override_property()
    {
        var initialNodeId = Guid.NewGuid();
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), CreateExecutableBlueprint(initialNodeId));
        var processSteps = new ProcessStepRepositorySpy();
        var processInstances = new ProcessInstanceRepositorySpy();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = new BlueprintProcessService(
            processSteps,
            CreateDispatchService(processSteps, queueWriter),
            new BlueprintExecutionCacheStub(CreateExecutableBlueprint(initialNodeId)),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance);

        var ex = await Should.ThrowAsync<InvalidDataException>(() => service.StartLatestVersionAsync(
            7,
            new BlueprintProcessInputModel
            {
                NodeOverrides = new Dictionary<Guid, Dictionary<string, JsonElement>>
                {
                    [initialNodeId] = new()
                    {
                        ["missing"] = Json("runtime")
                    }
                }
            },
            CancellationToken.None));

        ex.Message.ShouldContain("does not exist on blueprint node");
        processInstances.CreatedState.ShouldBeNull();
        processSteps.CreatedStep.ShouldBeNull();
        queueWriter.Messages.Count.ShouldBe(0);
    }

    [Test]
    public async Task StartLatestVersionAsync_applies_runtime_overrides_after_design_time_overrides()
    {
        var initialNodeId = Guid.NewGuid();
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), CreateExecutableBlueprint(initialNodeId));
        var processSteps = new ProcessStepRepositorySpy();
        var processInstances = new ProcessInstanceRepositorySpy();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = new BlueprintProcessService(
            processSteps,
            CreateDispatchService(processSteps, queueWriter),
            new BlueprintExecutionCacheStub(CreateExecutableBlueprint(initialNodeId)),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance);

        var accepted = await service.StartLatestVersionAsync(
            7,
            new BlueprintProcessInputModel
            {
                GlobalState = new Dictionary<string, JsonElement>
                {
                    ["region"] = Json("uk")
                },
                InitialTaskInput = new Dictionary<string, JsonElement>
                {
                    ["town"] = Json("input"),
                    ["country"] = Json("uk")
                },
                NodeOverrides = new Dictionary<Guid, Dictionary<string, JsonElement>>
                {
                    [initialNodeId] = new()
                    {
                        ["town"] = Json("runtime")
                    }
                }
            },
            CancellationToken.None);

        accepted.BlueprintId.ShouldBe(7);
        processInstances.CreatedState.ShouldNotBeNull();
        processInstances.CreatedState.NodeOverrides.ShouldContainKey(initialNodeId.ToString());
        processInstances.CreatedState.NodeOverrides[initialNodeId.ToString()]["town"].GetString().ShouldBe("runtime");

        processSteps.CreatedStep.ShouldNotBeNull();
        processSteps.CreatedStep.BlueprintNodeId.ShouldBe(initialNodeId.ToString());
        processInstances.CreatedState.ExecutionProgress.TotalNodeCount.ShouldBe(1);
        processSteps.CreatedStep.InputData.ShouldBeNull();
        processSteps.CreatedStep.InputHydrationPlan.ShouldNotBeNull();
        processSteps.CreatedStep.InputHydrationPlan.InputChannelName.ShouldBe("Input");
        processSteps.CreatedStep.InputHydrationPlan.InitialInput["town"].GetString().ShouldBe("input");
        processSteps.CreatedStep.InputHydrationPlan.InitialInput["country"].GetString().ShouldBe("uk");
        processSteps.CreatedStep.InputHydrationPlan.DesignTimeInputOverrides["town"].GetString().ShouldBe("design-time");
        processSteps.CreatedStep.InputHydrationPlan.DesignTimeInputOverrides["mode"].GetString().ShouldBe("design-time");
        processSteps.CreatedStep.InputHydrationPlan.RuntimeInputOverrides["town"].GetString().ShouldBe("runtime");

        queueWriter.Messages.Count.ShouldBe(1);
        queueWriter.Messages[0].ProcessId.ShouldBe(accepted.ProcessId);
        queueWriter.Messages[0].ProcessStepId.ShouldBe(Guid.Parse(processSteps.CreatedStep.Id));
        queueWriter.Messages[0].BlueprintNodeId.ShouldBe(initialNodeId);
    }

    [Test]
    public async Task StartLatestVersionAsync_accepts_when_immediate_dispatch_fails_after_intent_is_durable()
    {
        var initialNodeId = Guid.NewGuid();
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), CreateExecutableBlueprint(initialNodeId));
        var processSteps = new ProcessStepRepositorySpy();
        var processInstances = new ProcessInstanceRepositorySpy();
        var queueWriter = new ProcessStepQueueWriterSpy { ThrowOnEnqueueAttempts = 1 };
        var service = new BlueprintProcessService(
            processSteps,
            CreateDispatchService(processSteps, queueWriter),
            new BlueprintExecutionCacheStub(CreateExecutableBlueprint(initialNodeId)),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance);

        var accepted = await service.StartLatestVersionAsync(
            7,
            new BlueprintProcessInputModel(),
            CancellationToken.None);

        accepted.BlueprintId.ShouldBe(7);
        processSteps.CreatedStep.ShouldNotBeNull();
        processSteps.CreatedStep.Dispatch.ShouldNotBeNull();
        processSteps.CreatedStep.Dispatch.Status.ShouldBe(ProcessStepDispatchStatus.RetryableDispatchError);
        processSteps.CreatedStep.Dispatch.NextAttemptAt.ShouldNotBeNull();
        queueWriter.Messages.Count.ShouldBe(0);
    }

    [Test]
    public async Task StartLatestVersionAsync_fails_before_creating_process_when_dispatch_is_disabled()
    {
        var initialNodeId = Guid.NewGuid();
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), CreateExecutableBlueprint(initialNodeId));
        var processSteps = new ProcessStepRepositorySpy();
        var processInstances = new ProcessInstanceRepositorySpy();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = new BlueprintProcessService(
            processSteps,
            CreateDispatchService(processSteps, queueWriter, enabled: false),
            new BlueprintExecutionCacheStub(CreateExecutableBlueprint(initialNodeId)),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance);

        var ex = await Should.ThrowAsync<ProcessStepDispatchDisabledException>(() =>
            service.StartLatestVersionAsync(
                7,
                new BlueprintProcessInputModel(),
                CancellationToken.None));

        ex.Message.ShouldContain("dispatch is disabled");
        processInstances.CreatedState.ShouldBeNull();
        processSteps.CreatedStep.ShouldBeNull();
        queueWriter.Messages.Count.ShouldBe(0);
    }

    [Test]
    public async Task StartLatestVersionAsync_can_use_explicit_test_noop_dispatcher()
    {
        var initialNodeId = Guid.NewGuid();
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), CreateExecutableBlueprint(initialNodeId));
        var processSteps = new ProcessStepRepositorySpy();
        var processInstances = new ProcessInstanceRepositorySpy();
        var dispatcher = new TestNoOpProcessStepDispatcher();
        var service = new BlueprintProcessService(
            processSteps,
            dispatcher,
            new BlueprintExecutionCacheStub(CreateExecutableBlueprint(initialNodeId)),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance);

        var accepted = await service.StartLatestVersionAsync(
            7,
            new BlueprintProcessInputModel(),
            CancellationToken.None);

        accepted.BlueprintId.ShouldBe(7);
        processInstances.CreatedState.ShouldNotBeNull();
        processSteps.CreatedStep.ShouldNotBeNull();
        dispatcher.EnsureAvailableCalls.ShouldBe(1);
        dispatcher.EnsureIntentCalls.ShouldBe(1);
        dispatcher.TryDispatchCalls.ShouldBe(1);
    }

    [Test]
    public async Task StartLatestVersionAsync_persists_start_snapshot_for_initial_step_reconciliation()
    {
        var initialNodeId = Guid.NewGuid();
        var executableBlueprint = CreateExecutableBlueprint(initialNodeId);
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), executableBlueprint);
        var processSteps = new ProcessStepRepositorySpy();
        var processInstances = new ProcessInstanceRepositorySpy();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = new BlueprintProcessService(
            processSteps,
            CreateDispatchService(processSteps, queueWriter),
            new BlueprintExecutionCacheStub(executableBlueprint),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance);

        var accepted = await service.StartLatestVersionAsync(
            7,
            new BlueprintProcessInputModel
            {
                InitialTaskInput = new Dictionary<string, JsonElement>
                {
                    ["town"] = Json("input"),
                    ["country"] = Json("uk")
                },
                NodeOverrides = new Dictionary<Guid, Dictionary<string, JsonElement>>
                {
                    [initialNodeId] = new()
                    {
                        ["town"] = Json("runtime")
                    }
                }
            },
            CancellationToken.None);

        processInstances.CreatedState.ShouldNotBeNull();
        processInstances.CreatedState.Start.ShouldNotBeNull();
        processInstances.CreatedState.Start.InitialBlueprintNodeId.ShouldBe(initialNodeId.ToString());
        processInstances.CreatedState.Start.InitialProcessStepId.ShouldBe(
            ProcessStepIdentity.Create(accepted.ProcessId, initialNodeId).ToString());
        processInstances.CreatedState.Start.InitialInputHydrationPlan.InputChannelName.ShouldBe("Input");
        processInstances.CreatedState.Start.InitialInputHydrationPlan.InitialInput["town"].GetString().ShouldBe("input");
        processInstances.CreatedState.Start.InitialInputHydrationPlan.InitialInput["country"].GetString().ShouldBe("uk");
        processInstances.CreatedState.Start.InitialInputHydrationPlan.DesignTimeInputOverrides["town"].GetString().ShouldBe("design-time");
        processInstances.CreatedState.Start.InitialInputHydrationPlan.RuntimeInputOverrides["town"].GetString().ShouldBe("runtime");
    }

    [Test]
    public async Task StartLatestVersionAsync_keeps_start_snapshot_when_initial_step_creation_fails()
    {
        var initialNodeId = Guid.NewGuid();
        var executableBlueprint = CreateExecutableBlueprint(initialNodeId);
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), executableBlueprint);
        var processSteps = new ProcessStepRepositorySpy { ThrowOnCreateProcessStep = true };
        var processInstances = new ProcessInstanceRepositorySpy();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var service = new BlueprintProcessService(
            processSteps,
            CreateDispatchService(processSteps, queueWriter),
            new BlueprintExecutionCacheStub(executableBlueprint),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(() => service.StartLatestVersionAsync(
            7,
            new BlueprintProcessInputModel(),
            CancellationToken.None));

        processInstances.CreatedState.ShouldNotBeNull();
        processInstances.CreatedState.Start.ShouldNotBeNull();
        processInstances.CreatedState.Start.InitialBlueprintNodeId.ShouldBe(initialNodeId.ToString());
        Guid.Parse(processInstances.CreatedState.Start.InitialProcessStepId).ShouldBe(
            ProcessStepIdentity.Create(Guid.Parse(processInstances.CreatedState.ProcessId), initialNodeId));
        processSteps.CreatedStep.ShouldBeNull();
        queueWriter.Messages.Count.ShouldBe(0);
    }

    [Test]
    public async Task StartLatestVersionAsync_projects_process_directory_and_summaries_after_source_process_is_created()
    {
        var initialNodeId = Guid.NewGuid();
        var repository = new BlueprintRepositoryStub(CreateBlueprintViewModel(), CreateExecutableBlueprint(initialNodeId));
        var processSteps = new ProcessStepRepositorySpy();
        var processInstances = new ProcessInstanceRepositorySpy();
        var queueWriter = new ProcessStepQueueWriterSpy();
        var processIndex = new ProcessIndexProjectionServiceSpy();
        var service = new BlueprintProcessService(
            processSteps,
            CreateDispatchService(processSteps, queueWriter),
            new BlueprintExecutionCacheStub(CreateExecutableBlueprint(initialNodeId)),
            repository,
            processInstances,
            new ProcessStartTelemetry(),
            NullLogger<BlueprintProcessService>.Instance,
            processIndex);

        var accepted = await service.StartLatestVersionAsync(
            7,
            new BlueprintProcessInputModel(),
            CancellationToken.None);

        processIndex.Upserts.Count.ShouldBe(1);
        processIndex.Upserts[0].Process.ProcessId.ShouldBe(accepted.ProcessId.ToString());
        processIndex.Upserts[0].BlueprintDefinitionId.ShouldBe(7);
        processIndex.Upserts[0].BlueprintVersionId.ShouldBe(101);
    }

    private static ProcessStepDispatchService CreateDispatchService(
        ProcessStepRepositorySpy processSteps,
        ProcessStepQueueWriterSpy queueWriter,
        bool enabled = true) =>
        new(
            processSteps,
            new ProcessStepDispatchIntentRepositorySpy(),
            queueWriter,
            new ProcessStepDispatchOptions
            {
                Enabled = enabled,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                RetryMaxDelay = TimeSpan.FromMilliseconds(10),
                DueBucketDuration = TimeSpan.FromMilliseconds(1)
            },
            new ProcessStepDispatchTelemetry(),
            NullLogger<ProcessStepDispatchService>.Instance);

    private static BlueprintViewModel CreateBlueprintViewModel() =>
        new()
        {
            Id = 7,
            Key = "join-demo",
            Name = "Join Demo",
            Description = string.Empty,
            Versions =
            [
                new BlueprintVersionViewModel
                {
                    BlueprintDefinitionId = 7,
                    BlueprintVersionId = 101,
                    Version = 4,
                    StartingBlueprintNodeId = Guid.NewGuid(),
                    Tasks = []
                }
            ]
        };

    private static ExecutableBlueprint CreateExecutableBlueprint(Guid initialNodeId)
    {
        var node = new ExecutableBlueprintNode
        {
            Id = initialNodeId,
            Name = "Lookup",
            TaskDefinitionId = 42,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedVersion = new Version(1, 0, 0),
            ResolvedTaskVersionId = 4201,
            ExecutionOrder = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = "Input",
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["town"] = "String",
                    ["country"] = "String",
                    ["mode"] = "String"
                },
                OutboundBindings = []
            },
            OutputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal),
            InboundBindings = [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["town"] = Json("design-time"),
                ["mode"] = Json("design-time")
            }
        };

        return new ExecutableBlueprint
        {
            Id = 101,
            DefinitionId = 7,
            VersionNumber = 4,
            Name = "Join Demo",
            Description = null,
            InitialNode = node,
            AllNodes = [node]
        };
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private sealed class BlueprintRepositoryStub : IBlueprintRepository
    {
        private readonly BlueprintViewModel _blueprint;
        private readonly ExecutableBlueprint _executableBlueprint;

        public BlueprintRepositoryStub(BlueprintViewModel blueprint, ExecutableBlueprint executableBlueprint)
        {
            _blueprint = blueprint;
            _executableBlueprint = executableBlueprint;
        }

        public Task<ExecutableBlueprint> GetFullBlueprintById(int id, CancellationToken cancellationToken) =>
            Task.FromResult(_executableBlueprint);

        public Task<int> GetLatestBlueprintVersionId(int blueprintDefinitionId, CancellationToken cancellationToken) =>
            Task.FromResult(_executableBlueprint.Id);

        public Task<List<BlueprintViewModel>> GetAllBlueprints(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BlueprintViewModel> GetBlueprintById(int id, CancellationToken cancellationToken) =>
            Task.FromResult(_blueprint);

        public Task<BlueprintViewModel> CreateBlueprint(BlueprintInputModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BlueprintVersionViewModel> CreateVersion(BlueprintVersionInputModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BlueprintExecutionCacheStub : IBlueprintExecutionCache
    {
        private readonly ExecutableBlueprint _blueprint;

        public BlueprintExecutionCacheStub(ExecutableBlueprint blueprint)
        {
            _blueprint = blueprint;
        }

        public Task<ExecutableBlueprint> GetExecutableBlueprintAsync(int blueprintVersionId, CancellationToken cancellationToken) =>
            Task.FromResult(_blueprint);
    }

    private sealed class ProcessStepRepositorySpy : IProcessStepRepository
    {
        public ProcessStepRecord? CreatedStep { get; private set; }
        public bool ThrowOnCreateProcessStep { get; init; }

        public Task<ProcessStepRecord> CreateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken)
        {
            if (ThrowOnCreateProcessStep)
            {
                throw new InvalidOperationException("Initial process step creation failed.");
            }

            CreatedStep = state;
            return Task.FromResult(state);
        }

        public Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> GetProcessStepById(Guid taskInstanceId, Guid processId, CancellationToken cancellationToken)
        {
            if (CreatedStep is not null &&
                Guid.Parse(CreatedStep.Id) == taskInstanceId &&
                string.Equals(CreatedStep.ProcessId, processId.ToString(), StringComparison.Ordinal))
            {
                return Task.FromResult(CreatedStep);
            }

            throw new KeyNotFoundException($"Step {taskInstanceId} was not found.");
        }

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(Guid processId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProcessStepRecord>> GetDueStepDispatchesAsync(
            DateTimeOffset now,
            int maxItemCount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepDispatchBacklogSnapshot> GetDispatchBacklogSnapshotAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessStepDispatchBacklogSnapshot(
                CreatedStep is null ||
                    CreatedStep.Dispatch?.Status is ProcessStepDispatchStatus.Dispatched or ProcessStepDispatchStatus.DispatchSkippedTerminal ? 0 : 1,
                CreatedStep?.Dispatch?.CreatedAt ?? CreatedStep?.CreatedAt));

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimStepDispatchAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            if (CreatedStep is null ||
                CreatedStep.Status is not ProcessStepStatus.Pending ||
                CreatedStep.Dispatch?.Status is ProcessStepDispatchStatus.Dispatched)
            {
                return Task.FromResult<(bool, ProcessStepRecord?)>((false, null));
            }

            CreatedStep.Dispatch ??= ProcessStepDispatchService.CreatePendingDispatchState(CreatedStep.CreatedAt);
            CreatedStep.Dispatch.Status = ProcessStepDispatchStatus.Dispatching;
            CreatedStep.Dispatch.MessageId = CreatedStep.Id;
            CreatedStep.Dispatch.AttemptCount++;
            CreatedStep.Dispatch.UpdatedAt = now;
            CreatedStep.Dispatch.NextAttemptAt = null;
            CreatedStep.Dispatch.DispatchingStartedAt = now;
            CreatedStep.Dispatch.LockedUntil = now.Add(leaseDuration);
            CreatedStep.Dispatch.LeaseId = leaseId;
            return Task.FromResult<(bool, ProcessStepRecord?)>((true, CreatedStep));
        }

        public Task<bool> MarkStepDispatchSucceededAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset dispatchedAt,
            CancellationToken cancellationToken)
        {
            if (CreatedStep?.Dispatch is null ||
                !string.Equals(CreatedStep.Dispatch.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            CreatedStep.Dispatch.Status = ProcessStepDispatchStatus.Dispatched;
            CreatedStep.Dispatch.MessageId = CreatedStep.Id;
            CreatedStep.Dispatch.UpdatedAt = dispatchedAt;
            CreatedStep.Dispatch.DispatchedAt = dispatchedAt;
            CreatedStep.Dispatch.NextAttemptAt = null;
            CreatedStep.Dispatch.DispatchingStartedAt = null;
            CreatedStep.Dispatch.LockedUntil = null;
            CreatedStep.Dispatch.LeaseId = null;
            CreatedStep.Dispatch.LastErrorKind = null;
            CreatedStep.Dispatch.LastErrorMessage = null;
            CreatedStep.Dispatch.LastErrorAt = null;
            return Task.FromResult(true);
        }

        public Task<bool> MarkStepDispatchRetryableErrorAsync(
            ProcessStepRecord state,
            string leaseId,
            ProcessStepDispatchErrorKind errorKind,
            string errorMessage,
            DateTimeOffset failedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken)
        {
            if (CreatedStep?.Dispatch is null ||
                !string.Equals(CreatedStep.Dispatch.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            CreatedStep.Dispatch.Status = ProcessStepDispatchStatus.RetryableDispatchError;
            CreatedStep.Dispatch.UpdatedAt = failedAt;
            CreatedStep.Dispatch.NextAttemptAt = nextAttemptAt;
            CreatedStep.Dispatch.DispatchingStartedAt = null;
            CreatedStep.Dispatch.LockedUntil = null;
            CreatedStep.Dispatch.LeaseId = null;
            CreatedStep.Dispatch.LastErrorKind = errorKind;
            CreatedStep.Dispatch.LastErrorMessage = errorMessage;
            CreatedStep.Dispatch.LastErrorAt = failedAt;
            return Task.FromResult(true);
        }

        public Task<ProcessStepRecord> UpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateStepDispatchAsync(
            Guid processStepId,
            Guid processId,
            ProcessStepDispatchState dispatch,
            CancellationToken cancellationToken)
        {
            if (CreatedStep is null ||
                Guid.Parse(CreatedStep.Id) != processStepId ||
                !string.Equals(CreatedStep.ProcessId, processId.ToString(), StringComparison.Ordinal) ||
                CreatedStep.Status is not ProcessStepStatus.Pending)
            {
                return Task.FromResult(false);
            }

            CreatedStep.Dispatch = dispatch;
            CreatedStep.UpdatedAt = dispatch.UpdatedAt;
            return Task.FromResult(true);
        }

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

    private sealed class ProcessStepDispatchIntentRepositorySpy : IProcessStepDispatchIntentRepository
    {
        private readonly Dictionary<(string Id, string PartitionKey), ProcessStepDispatchIntentRecord> _intents = [];
        private readonly Dictionary<int, ProcessStepDispatchCursorRecord> _cursors = [];

        public Task<ProcessStepDispatchIntentRecord?> GetIntentAsync(
            string id,
            string partitionKey,
            CancellationToken cancellationToken)
        {
            _intents.TryGetValue((id, partitionKey), out var record);
            return Task.FromResult(record);
        }

        public Task<(bool Created, ProcessStepDispatchIntentRecord Record)> CreateIntentIfNotExistsAsync(
            ProcessStepDispatchIntentRecord record,
            CancellationToken cancellationToken)
        {
            var key = (record.Id, record.PartitionKey);
            if (_intents.TryGetValue(key, out var existing))
            {
                return Task.FromResult((false, existing));
            }

            _intents[key] = record;
            return Task.FromResult((true, record));
        }

        public Task<IReadOnlyList<ProcessStepDispatchIntentRecord>> GetDueIntentsAsync(
            long dueBucket,
            int shard,
            int maxItemCount,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var partitionKey = ProcessStepDispatchIntentKeys.CreateDuePartitionKey(dueBucket, shard);
            return Task.FromResult<IReadOnlyList<ProcessStepDispatchIntentRecord>>(_intents.Values
                .Where(record => string.Equals(record.PartitionKey, partitionKey, StringComparison.Ordinal) &&
                    IsDue(record, now))
                .OrderBy(record => record.CreatedAt)
                .Take(maxItemCount)
                .ToList());
        }

        public Task<(bool Claimed, ProcessStepDispatchIntentRecord? Record)> TryClaimIntentAsync(
            ProcessStepDispatchIntentRecord record,
            string leaseId,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            if (!_intents.TryGetValue((record.Id, record.PartitionKey), out var current) ||
                !IsDue(current, now))
            {
                return Task.FromResult<(bool, ProcessStepDispatchIntentRecord?)>((false, null));
            }

            current.Status = ProcessStepDispatchStatus.Dispatching;
            current.MessageId = current.ProcessStepId;
            current.AttemptCount++;
            current.UpdatedAt = now;
            current.DispatchingStartedAt = now;
            current.LockedUntil = now.Add(leaseDuration);
            current.LeaseId = leaseId;
            return Task.FromResult<(bool, ProcessStepDispatchIntentRecord?)>((true, current));
        }

        public Task<bool> MarkIntentDispatchedAsync(
            ProcessStepDispatchIntentRecord record,
            string leaseId,
            DateTimeOffset dispatchedAt,
            CancellationToken cancellationToken)
        {
            if (!TryGetLeased(record, leaseId, out var current))
            {
                return Task.FromResult(false);
            }

            current.Status = ProcessStepDispatchStatus.Dispatched;
            current.MessageId = current.ProcessStepId;
            current.UpdatedAt = dispatchedAt;
            current.DispatchedAt = dispatchedAt;
            current.DispatchingStartedAt = null;
            current.LockedUntil = null;
            current.LeaseId = null;
            current.LastErrorKind = null;
            current.LastErrorMessage = null;
            current.LastErrorAt = null;
            return Task.FromResult(true);
        }

        public Task<bool> MarkIntentRetryableErrorAsync(
            ProcessStepDispatchIntentRecord record,
            string leaseId,
            ProcessStepDispatchErrorKind errorKind,
            string errorMessage,
            DateTimeOffset failedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken)
        {
            if (!TryGetLeased(record, leaseId, out var current))
            {
                return Task.FromResult(false);
            }

            current.Status = ProcessStepDispatchStatus.RetryableDispatchError;
            current.UpdatedAt = failedAt;
            current.NextAttemptAt = nextAttemptAt;
            current.DispatchingStartedAt = null;
            current.LockedUntil = null;
            current.LeaseId = null;
            current.LastErrorKind = errorKind;
            current.LastErrorMessage = errorMessage;
            current.LastErrorAt = failedAt;
            return Task.FromResult(true);
        }

        public Task<bool> MarkIntentSkippedTerminalAsync(
            ProcessStepDispatchIntentRecord record,
            string leaseId,
            DateTimeOffset skippedAt,
            CancellationToken cancellationToken)
        {
            if (!TryGetLeased(record, leaseId, out var current))
            {
                return Task.FromResult(false);
            }

            current.Status = ProcessStepDispatchStatus.DispatchSkippedTerminal;
            current.UpdatedAt = skippedAt;
            current.DispatchingStartedAt = null;
            current.LockedUntil = null;
            current.LeaseId = null;
            return Task.FromResult(true);
        }

        public Task<ProcessStepDispatchCursorRecord> GetOrCreateCursorAsync(
            int shard,
            long initialDueBucket,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken)
        {
            if (_cursors.TryGetValue(shard, out var cursor))
            {
                return Task.FromResult(cursor);
            }

            cursor = new ProcessStepDispatchCursorRecord
            {
                id = ProcessStepDispatchIntentKeys.CreateCursorId(shard),
                PartitionKey = ProcessStepDispatchIntentKeys.CreateCursorPartitionKey(shard),
                Shard = shard,
                NextDueBucket = initialDueBucket,
                CreatedAt = timestamp,
                UpdatedAt = timestamp
            };
            _cursors[shard] = cursor;
            return Task.FromResult(cursor);
        }

        public Task<bool> TryAdvanceCursorAsync(
            ProcessStepDispatchCursorRecord cursor,
            long nextDueBucket,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken)
        {
            cursor.NextDueBucket = Math.Max(cursor.NextDueBucket, nextDueBucket);
            cursor.UpdatedAt = timestamp;
            return Task.FromResult(true);
        }

        public async Task<bool> TryRewindCursorAsync(
            int shard,
            long nextDueBucket,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken)
        {
            var cursor = await GetOrCreateCursorAsync(shard, nextDueBucket, timestamp, cancellationToken);
            if (nextDueBucket < cursor.NextDueBucket)
            {
                cursor.NextDueBucket = nextDueBucket;
                cursor.UpdatedAt = timestamp;
            }

            return true;
        }

        public Task<ProcessStepDispatchBacklogSnapshot> GetBacklogSnapshotAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var active = _intents.Values
                .Where(record => record.Status is ProcessStepDispatchStatus.Pending
                    or ProcessStepDispatchStatus.RetryableDispatchError
                    or ProcessStepDispatchStatus.Dispatching)
                .OrderBy(record => record.CreatedAt)
                .ToList();
            return Task.FromResult(new ProcessStepDispatchBacklogSnapshot(
                active.Count,
                active.FirstOrDefault()?.CreatedAt));
        }

        private bool TryGetLeased(
            ProcessStepDispatchIntentRecord record,
            string leaseId,
            out ProcessStepDispatchIntentRecord current)
        {
            current = null!;
            if (!_intents.TryGetValue((record.Id, record.PartitionKey), out var existing) ||
                !string.Equals(existing.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return false;
            }

            current = existing;
            return true;
        }

        private static bool IsDue(ProcessStepDispatchIntentRecord record, DateTimeOffset now) =>
            ((record.Status is ProcessStepDispatchStatus.Pending or ProcessStepDispatchStatus.RetryableDispatchError) &&
                record.NextAttemptAt <= now) ||
            record.Status is ProcessStepDispatchStatus.Dispatching &&
                record.LockedUntil.HasValue &&
                record.LockedUntil.Value <= now;
    }

    private sealed class TestNoOpProcessStepDispatcher : IProcessStepDispatcher
    {
        public int EnsureAvailableCalls { get; private set; }
        public int EnsureIntentCalls { get; private set; }
        public int TryDispatchCalls { get; private set; }

        public void EnsureDispatchAvailableForExecution()
        {
            EnsureAvailableCalls++;
        }

        public Task<ProcessStepDispatchIntentRecord> EnsureDispatchIntentAsync(
            ProcessStepRecord step,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken)
        {
            EnsureIntentCalls++;
            var dueBucket = ProcessStepDispatchService.CalculateDueBucket(timestamp, TimeSpan.FromSeconds(5));
            var shard = ProcessStepDispatchService.CalculateShard(step.Id, 1);
            return Task.FromResult(new ProcessStepDispatchIntentRecord
            {
                id = ProcessStepDispatchService.CreateIntentId(step.Id, dueBucket, 0),
                PartitionKey = ProcessStepDispatchIntentKeys.CreateDuePartitionKey(dueBucket, shard),
                ProcessStepId = step.Id,
                ProcessId = step.ProcessId,
                BlueprintId = step.BlueprintId,
                BlueprintNodeId = step.BlueprintNodeId,
                Status = ProcessStepDispatchStatus.Pending,
                DueBucket = dueBucket,
                Shard = shard,
                NextAttemptAt = timestamp,
                AttemptCount = 0,
                CreatedAt = timestamp,
                UpdatedAt = timestamp
            });
        }

        public Task<bool> TryDispatchAsync(
            ProcessStepRecord step,
            CancellationToken cancellationToken)
        {
            TryDispatchCalls++;
            return Task.FromResult(false);
        }

        public Task<bool> TryDispatchAsync(
            ProcessStepRecord step,
            ProcessStepDispatchIntentRecord intent,
            CancellationToken cancellationToken)
        {
            TryDispatchCalls++;
            return Task.FromResult(false);
        }

        public Task<int> DrainDueDispatchesAsync(
            int maxItemCount,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task RefreshBacklogMetricsAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ProcessInstanceRepositorySpy : IProcessInstanceRepository<Dictionary<string, JsonElement>>
    {
        public ProcessInstanceRecord<Dictionary<string, JsonElement>>? CreatedState { get; private set; }

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> CreateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken)
        {
            CreatedState = state;
            return Task.FromResult(state);
        }

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

    private sealed class ProcessStepQueueWriterSpy : IProcessStepQueueWriter
    {
        public List<ProcessStepInitialisationMessage> Messages { get; } = [];
        public int ThrowOnEnqueueAttempts { get; set; }

        public Task EnqueueAsync(ProcessStepInitialisationMessage message, CancellationToken cancellationToken)
        {
            if (ThrowOnEnqueueAttempts > 0)
            {
                ThrowOnEnqueueAttempts--;
                throw new InvalidOperationException("Queue enqueue failed.");
            }

            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ProcessIndexProjectionServiceSpy : IProcessIndexProjectionService
    {
        public List<(ProcessInstanceRecord<Dictionary<string, JsonElement>> Process, int BlueprintDefinitionId, int BlueprintVersionId)> Upserts { get; } = [];

        public Task UpsertFromProcessAsync(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            Upserts.Add((process, blueprintDefinitionId, blueprintVersionId));
            return Task.CompletedTask;
        }

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
}
