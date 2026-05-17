using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Services.ProcessStepQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class ActivationPersistenceServiceTests
{
    [Test]
    public async Task Single_inbound_downstream_step_is_created_with_source_reference_hydration_plan()
    {
        var processId = Guid.NewGuid();
        var sourceNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var bindingId = "source-to-destination";
        var blueprint = CreateSingleInboundBlueprint(sourceNodeId, destinationNodeId, bindingId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(
            processId,
            blueprint,
            new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
            {
                [destinationNodeId.ToString()] = new(StringComparer.Ordinal)
                {
                    ["Mode"] = Json("runtime")
                }
            }));
        var queueWriter = new ProcessStepQueueWriterSpy();
        var context = await CreateClaimedContextAsync(
            processId,
            sourceNodeId,
            blueprint,
            stepRepository,
            processRepository);
        var service = new ActivationPersistenceService(
            new ActivationPlanner(),
            new InMemoryNodeJoinStateRepository(),
            CreateDispatchService(stepRepository, queueWriter),
            stepRepository,
            NullLogger<ActivationPersistenceService>.Instance);

        await service.PersistReadyActivationsAsync(
            context,
            CreateResult(context, "Out"),
            CancellationToken.None);

        stepRepository.CreatedSteps.Count.ShouldBe(1);
        var createdStep = stepRepository.CreatedSteps[0];
        createdStep.BlueprintNodeId.ShouldBe(destinationNodeId.ToString());
        createdStep.InputData.ShouldBeNull();
        createdStep.InputHydrationPlan.ShouldNotBeNull();
        createdStep.InputHydrationPlan.InputChannelName.ShouldBe("DestinationInput");
        createdStep.InputHydrationPlan.InitialInput.Count.ShouldBe(0);
        createdStep.InputHydrationPlan.Sources.Count.ShouldBe(1);
        createdStep.InputHydrationPlan.Sources[0].BindingId.ShouldBe(bindingId);
        createdStep.InputHydrationPlan.Sources[0].SourceProcessStepId.ShouldBe(context.ProcessStepId.ToString());
        createdStep.InputHydrationPlan.Sources[0].SourceOutputChannelName.ShouldBe("Out");
        createdStep.InputHydrationPlan.Sources[0].PropertyMappings.Single().SourceProperty.ShouldBe("Message");
        createdStep.InputHydrationPlan.Sources[0].PropertyMappings.Single().TargetProperty.ShouldBe("Text");
        createdStep.InputHydrationPlan.DesignTimeInputOverrides["Mode"].GetString().ShouldBe("design");
        createdStep.InputHydrationPlan.RuntimeInputOverrides["Mode"].GetString().ShouldBe("runtime");

        queueWriter.Messages.Count.ShouldBe(1);
        queueWriter.Messages[0].ProcessStepId.ShouldBe(ProcessStepIdentity.Create(processId, destinationNodeId));
        stepRepository.ProcessStepListFetchCount.ShouldBe(0);
    }

    [Test]
    public async Task Multi_inbound_join_waits_for_all_bindings_then_enqueues_once_with_source_references()
    {
        var processId = Guid.NewGuid();
        var leftNodeId = Guid.NewGuid();
        var rightNodeId = Guid.NewGuid();
        var joinNodeId = Guid.NewGuid();
        var blueprint = CreateJoinBlueprint(leftNodeId, rightNodeId, joinNodeId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(
            processId,
            blueprint,
            new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)));
        var queueWriter = new ProcessStepQueueWriterSpy();
        var joinRepository = new InMemoryNodeJoinStateRepository();
        var service = new ActivationPersistenceService(
            new ActivationPlanner(),
            joinRepository,
            CreateDispatchService(stepRepository, queueWriter),
            stepRepository,
            NullLogger<ActivationPersistenceService>.Instance);

        var leftContext = await CreateClaimedContextAsync(
            processId,
            leftNodeId,
            blueprint,
            stepRepository,
            processRepository);

        await service.PersistReadyActivationsAsync(
            leftContext,
            CreateResult(leftContext, "LeftOut"),
            CancellationToken.None);

        stepRepository.CreatedSteps.Count.ShouldBe(0);
        queueWriter.Messages.Count.ShouldBe(0);

        var rightContext = await CreateClaimedContextAsync(
            processId,
            rightNodeId,
            blueprint,
            stepRepository,
            processRepository);

        await service.PersistReadyActivationsAsync(
            rightContext,
            CreateResult(rightContext, "RightOut"),
            CancellationToken.None);

        stepRepository.CreatedSteps.Count.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
        var createdStep = stepRepository.CreatedSteps[0];
        createdStep.BlueprintNodeId.ShouldBe(joinNodeId.ToString());
        createdStep.InputHydrationPlan.ShouldNotBeNull();
        createdStep.InputHydrationPlan.Sources.Count.ShouldBe(2);
        createdStep.InputHydrationPlan.Sources.Select(source => source.BindingId).ShouldBe([
            "left-to-join",
            "right-to-join"
        ]);
        createdStep.InputHydrationPlan.Sources[0].SourceProcessStepId.ShouldBe(leftContext.ProcessStepId.ToString());
        createdStep.InputHydrationPlan.Sources[1].SourceProcessStepId.ShouldBe(rightContext.ProcessStepId.ToString());
        joinRepository.States[(processId, joinNodeId)].StepEnqueued.ShouldBeTrue();
        joinRepository.States[(processId, joinNodeId)].EnqueuedProcessStepId.ShouldBe(createdStep.Id);
        stepRepository.ProcessStepListFetchCount.ShouldBe(0);

        await service.PersistReadyActivationsAsync(
            rightContext,
            CreateResult(rightContext, "RightOut"),
            CancellationToken.None);

        stepRepository.CreatedSteps.Count.ShouldBe(1);
        queueWriter.Messages.Count.ShouldBe(1);
    }

    [Test]
    public async Task Collection_iterator_fan_out_creates_item_scoped_steps_with_inline_inputs()
    {
        var processId = Guid.NewGuid();
        var iteratorNodeId = Guid.NewGuid();
        var destinationNodeId = Guid.NewGuid();
        var bindingId = "iterator-to-destination";
        var blueprint = CreateCollectionIteratorBlueprint(iteratorNodeId, destinationNodeId, bindingId);
        var stepRepository = new InMemoryProcessStepRepository();
        var processRepository = new InMemoryProcessInstanceRepository(CreateProcessState(
            processId,
            blueprint,
            new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)));
        var queueWriter = new ProcessStepQueueWriterSpy();
        var context = await CreateClaimedContextAsync(
            processId,
            iteratorNodeId,
            blueprint,
            stepRepository,
            processRepository);
        var service = new ActivationPersistenceService(
            new ActivationPlanner(),
            new InMemoryNodeJoinStateRepository(),
            CreateDispatchService(stepRepository, queueWriter),
            stepRepository,
            NullLogger<ActivationPersistenceService>.Instance);

        var result = await service.PersistCollectionIteratorItemActivationsAsync(
            context,
            [Json("alpha"), Json("bravo")],
            batchSize: 100,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        result.ScheduledStepCount.ShouldBe(2);
        stepRepository.CreatedSteps.Count.ShouldBe(2);
        queueWriter.Messages.Count.ShouldBe(2);
        for (var index = 0; index < 2; index++)
        {
            var itemKey = $"{context.ProcessStepId:N}:{index:D8}";
            var expectedStepId = ProcessStepIdentity.Create(processId, destinationNodeId, itemKey);
            var createdStep = stepRepository.CreatedSteps[index];
            createdStep.Id.ShouldBe(expectedStepId.ToString());
            createdStep.IterationContext.ShouldNotBeNull();
            createdStep.IterationContext.ItemIndex.ShouldBe(index);
            createdStep.IterationContext.ItemKey.ShouldBe(itemKey);
            createdStep.InputHydrationPlan.ShouldNotBeNull();
            createdStep.InputHydrationPlan.Sources.Single().InlineInput.ShouldNotBeNull();
            createdStep.InputHydrationPlan.Sources.Single().InlineInput!["Text"].GetString()
                .ShouldBe(index == 0 ? "alpha" : "bravo");
            queueWriter.Messages[index].ProcessStepId.ShouldBe(expectedStepId);
        }
    }

    private static async Task<StepExecutionContext> CreateClaimedContextAsync(
        Guid processId,
        Guid nodeId,
        ExecutableBlueprint blueprint,
        InMemoryProcessStepRepository stepRepository,
        InMemoryProcessInstanceRepository processRepository)
    {
        var processStepId = ProcessStepIdentity.Create(processId, nodeId);
        var node = blueprint.NodesById[nodeId];
        stepRepository.States[processStepId] = new ProcessStepRecord
        {
            Id = processStepId.ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            BlueprintNodeId = nodeId.ToString(),
            Status = ProcessStepStatus.Pending,
            InputHydrationPlan = new InputHydrationPlan
            {
                InputChannelName = node.InputChannel.Name
            }
        };

        var message = new ProcessStepInitialisationMessage
        {
            ProcessId = processId,
            ProcessStepId = processStepId,
            ExecutableBlueprintId = blueprint.Id,
            BlueprintNodeId = nodeId
        };

        var context = new StepExecutionContext(
            message,
            NullLogger<StepExecutionContext>.Instance,
            new ProcessStepQueueMessageStub(message),
            blueprint);

        var claim = await new StepClaimService(
                stepRepository,
                processRepository,
                new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance))
            .TryClaimStepAsync(context, CancellationToken.None);
        claim.StepClaimed.ShouldBeTrue();
        return context;
    }

    private static ProcessStepDispatchService CreateDispatchService(
        InMemoryProcessStepRepository stepRepository,
        ProcessStepQueueWriterSpy queueWriter) =>
        new(
            stepRepository,
            queueWriter,
            new ProcessStepDispatchOptions
            {
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                RetryMaxDelay = TimeSpan.FromMilliseconds(10)
            },
            new ProcessStepDispatchTelemetry(),
            NullLogger<ProcessStepDispatchService>.Instance);

    private static PluginExecutionResult CreateResult(StepExecutionContext context, string outputChannelName) =>
        new(
            context.ProcessStepId,
            Succeeded: true,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [outputChannelName] = Json(new
                {
                    Message = "hello",
                    Score = 7
                })
            },
            GlobalState: null,
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: null);

    private static ExecutableBlueprint CreateSingleInboundBlueprint(
        Guid sourceNodeId,
        Guid destinationNodeId,
        string bindingId)
    {
        var binding = new ExecutableBlueprintBinding
        {
            Id = bindingId,
            SourceBlueprintNodeId = sourceNodeId,
            SourceChannelName = "Out",
            DestinationBlueprintNodeId = destinationNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "Message",
                    TargetProperty = "Text"
                }
            ]
        };

        var source = CreateNode(sourceNodeId, "SourceInput", "Out", [binding]);
        var destination = CreateNode(destinationNodeId, "DestinationInput", null, [], [binding]);
        destination.DesignTimeInputOverrides["Mode"] = Json("design");

        return CreateBlueprint(source, [source, destination]);
    }

    private static ExecutableBlueprint CreateJoinBlueprint(
        Guid leftNodeId,
        Guid rightNodeId,
        Guid joinNodeId)
    {
        var leftBinding = new ExecutableBlueprintBinding
        {
            Id = "left-to-join",
            SourceBlueprintNodeId = leftNodeId,
            SourceChannelName = "LeftOut",
            DestinationBlueprintNodeId = joinNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "Message",
                    TargetProperty = "LeftText"
                }
            ]
        };
        var rightBinding = new ExecutableBlueprintBinding
        {
            Id = "right-to-join",
            SourceBlueprintNodeId = rightNodeId,
            SourceChannelName = "RightOut",
            DestinationBlueprintNodeId = joinNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "Score",
                    TargetProperty = "Score"
                }
            ]
        };

        var left = CreateNode(leftNodeId, "LeftInput", "LeftOut", [leftBinding]);
        var right = CreateNode(rightNodeId, "RightInput", "RightOut", [rightBinding]);
        var join = CreateNode(joinNodeId, "JoinInput", null, [], [leftBinding, rightBinding]);

        return CreateBlueprint(left, [left, right, join]);
    }

    private static ExecutableBlueprint CreateTerminalBlueprint(Guid nodeId)
    {
        var node = CreateNode(nodeId, "Input", null, []);
        return CreateBlueprint(node, [node]);
    }

    private static ExecutableBlueprint CreateCollectionIteratorBlueprint(
        Guid iteratorNodeId,
        Guid destinationNodeId,
        string bindingId)
    {
        var binding = new ExecutableBlueprintBinding
        {
            Id = bindingId,
            SourceBlueprintNodeId = iteratorNodeId,
            SourceChannelName = "Item",
            DestinationBlueprintNodeId = destinationNodeId,
            PropertyMappings =
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "item",
                    TargetProperty = "Text"
                }
            ]
        };
        var iterator = new ExecutableBlueprintNode
        {
            Id = iteratorNodeId,
            Name = "Collection Iterator",
            NodeKind = ExecutableBlueprintNodeKind.CollectionIterator,
            TaskDefinitionId = 0,
            CollectionIteratorItemKind = "String",
            AllowedMajor = 0,
            AllowedMinor = 0,
            ResolvedVersion = new Version(0, 0, 0),
            ResolvedTaskVersionId = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 0,
                Name = "Collection",
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["items"] = "String[]"
                },
                OutboundBindings = []
            },
            OutputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal)
            {
                ["Item"] = new()
                {
                    Id = 0,
                    Name = "Item",
                    Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["item"] = "String"
                    },
                    OutboundBindings = [binding]
                }
            },
            InboundBindings = [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };
        var destination = CreateNode(destinationNodeId, "DestinationInput", null, [], [binding]);

        return CreateBlueprint(iterator, [iterator, destination]);
    }

    private static ExecutableBlueprintNode CreateNode(
        Guid nodeId,
        string inputChannelName,
        string? outputChannelName,
        List<ExecutableBlueprintBinding> outboundBindings,
        List<ExecutableBlueprintBinding>? inboundBindings = null)
    {
        var outputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal);
        if (outputChannelName is not null)
        {
            outputChannels[outputChannelName] = new ExecutableBlueprintChannel
            {
                Id = outputChannels.Count + 1,
                Name = outputChannelName,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Message"] = "String",
                    ["Score"] = "Integer"
                },
                OutboundBindings = outboundBindings
            };
        }

        return new ExecutableBlueprintNode
        {
            Id = nodeId,
            Name = inputChannelName,
            TaskDefinitionId = 10,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedVersion = new Version(1, 0, 0),
            ResolvedTaskVersionId = 100,
            ExecutionOrder = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = inputChannelName,
                Properties = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Text"] = "String",
                    ["Mode"] = "String",
                    ["LeftText"] = "String",
                    ["Score"] = "Integer"
                },
                OutboundBindings = []
            },
            OutputChannels = outputChannels,
            InboundBindings = inboundBindings ?? [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };
    }

    private static ExecutableBlueprint CreateBlueprint(
        ExecutableBlueprintNode initialNode,
        List<ExecutableBlueprintNode> nodes) =>
        new()
        {
            Id = 101,
            DefinitionId = 10,
            VersionNumber = 1,
            Name = "Activation Blueprint",
            InitialNode = initialNode,
            AllNodes = nodes
        };

    private static ProcessInstanceRecord<Dictionary<string, JsonElement>> CreateProcessState(
        Guid processId,
        ExecutableBlueprint blueprint,
        Dictionary<string, Dictionary<string, JsonElement>> nodeOverrides) =>
        new()
        {
            id = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            Status = ProcessStatus.InProgress,
            GlobalState = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            NodeOverrides = nodeOverrides,
            ExecutionProgress = new ProcessExecutionProgress
            {
                TotalNodeCount = blueprint.AllNodes.Count
            }
        };

    private static JsonElement Json<T>(T value) =>
        JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private sealed class InMemoryProcessStepRepository : IProcessStepRepository
    {
        public Dictionary<Guid, ProcessStepRecord> States { get; } = new();
        public List<ProcessStepRecord> CreatedSteps { get; } = [];
        public int ProcessStepListFetchCount { get; private set; }

        public Task<ProcessStepRecord> CreateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken)
        {
            States[Guid.Parse(state.Id)] = state;
            CreatedSteps.Add(state);
            return Task.FromResult(state);
        }

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
            CreatedSteps.Add(state);
            return Task.FromResult((true, state));
        }

        public Task<ProcessStepRecord> GetProcessStepById(
            Guid taskInstanceId,
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(States[taskInstanceId]);

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
            Guid processId,
            CancellationToken cancellationToken)
        {
            ProcessStepListFetchCount++;
            return Task.FromResult(States.Values
                .Where(state => string.Equals(state.ProcessId, processId.ToString(), StringComparison.Ordinal))
                .ToList());
        }

        public Task<IReadOnlyList<ProcessStepRecord>> GetDueStepDispatchesAsync(
            DateTimeOffset now,
            int maxItemCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProcessStepRecord>>(States.Values
                .Where(state => state.Status is ProcessStepStatus.Pending && IsDispatchDue(state.Dispatch, now))
                .OrderBy(state => state.CreatedAt)
                .Take(maxItemCount)
                .ToList());

        public Task<ProcessStepDispatchBacklogSnapshot> GetDispatchBacklogSnapshotAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var pending = States.Values
                .Where(state => state.Status is ProcessStepStatus.Pending &&
                    (state.Dispatch is null ||
                     state.Dispatch.Status is ProcessStepDispatchStatus.Pending or ProcessStepDispatchStatus.Dispatching or ProcessStepDispatchStatus.RetryableDispatchError))
                .ToList();
            return Task.FromResult(new ProcessStepDispatchBacklogSnapshot(
                pending.Count,
                pending.OrderBy(state => state.CreatedAt).FirstOrDefault()?.Dispatch?.CreatedAt ??
                    pending.OrderBy(state => state.CreatedAt).FirstOrDefault()?.CreatedAt));
        }

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimStepDispatchAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            var processStepId = Guid.Parse(state.Id);
            if (!States.TryGetValue(processStepId, out var current) ||
                current.Status is not ProcessStepStatus.Pending ||
                !IsDispatchDue(current.Dispatch, now))
            {
                return Task.FromResult<(bool, ProcessStepRecord?)>((false, null));
            }

            current.Dispatch ??= ProcessStepDispatchService.CreatePendingDispatchState(current.CreatedAt);
            current.Dispatch.Status = ProcessStepDispatchStatus.Dispatching;
            current.Dispatch.MessageId = current.Id;
            current.Dispatch.AttemptCount++;
            current.Dispatch.UpdatedAt = now;
            current.Dispatch.NextAttemptAt = null;
            current.Dispatch.DispatchingStartedAt = now;
            current.Dispatch.LockedUntil = now.Add(leaseDuration);
            current.Dispatch.LeaseId = leaseId;
            return Task.FromResult<(bool, ProcessStepRecord?)>((true, current));
        }

        public Task<bool> MarkStepDispatchSucceededAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset dispatchedAt,
            CancellationToken cancellationToken)
        {
            if (!TryGetLeasedDispatch(state, leaseId, out var dispatch))
            {
                return Task.FromResult(false);
            }

            dispatch.Status = ProcessStepDispatchStatus.Dispatched;
            dispatch.MessageId = state.Id;
            dispatch.UpdatedAt = dispatchedAt;
            dispatch.DispatchedAt = dispatchedAt;
            dispatch.NextAttemptAt = null;
            dispatch.DispatchingStartedAt = null;
            dispatch.LockedUntil = null;
            dispatch.LeaseId = null;
            dispatch.LastErrorKind = null;
            dispatch.LastErrorMessage = null;
            dispatch.LastErrorAt = null;
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
            if (!TryGetLeasedDispatch(state, leaseId, out var dispatch))
            {
                return Task.FromResult(false);
            }

            dispatch.Status = ProcessStepDispatchStatus.RetryableDispatchError;
            dispatch.UpdatedAt = failedAt;
            dispatch.NextAttemptAt = nextAttemptAt;
            dispatch.DispatchingStartedAt = null;
            dispatch.LockedUntil = null;
            dispatch.LeaseId = null;
            dispatch.LastErrorKind = errorKind;
            dispatch.LastErrorMessage = errorMessage;
            dispatch.LastErrorAt = failedAt;
            return Task.FromResult(true);
        }

        public Task<bool> MarkStepDispatchSkippedTerminalAsync(
            ProcessStepRecord state,
            string leaseId,
            DateTimeOffset skippedAt,
            CancellationToken cancellationToken)
        {
            if (!TryGetLeasedDispatch(state, leaseId, out var dispatch))
            {
                return Task.FromResult(false);
            }

            dispatch.Status = ProcessStepDispatchStatus.DispatchSkippedTerminal;
            dispatch.UpdatedAt = skippedAt;
            dispatch.NextAttemptAt = null;
            dispatch.DispatchingStartedAt = null;
            dispatch.LockedUntil = null;
            dispatch.LeaseId = null;
            return Task.FromResult(true);
        }

        public Task<ProcessStepRecord> UpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken)
        {
            States[Guid.Parse(state.Id)] = state;
            return Task.FromResult(state);
        }

        public Task<bool> TryUpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken)
        {
            States[Guid.Parse(state.Id)] = state;
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
            CancellationToken cancellationToken)
        {
            if (!States.TryGetValue(taskInstanceId, out var state) ||
                state.Status is not ProcessStepStatus.Pending)
            {
                return Task.FromResult<(bool, ProcessStepRecord?)>((false, null));
            }

            state.Status = ProcessStepStatus.InProgress;
            state.TaskDefinitionId = taskDefinitionId;
            state.AllowedMajor = allowedMajor;
            state.AllowedMinor = allowedMinor;
            state.ResolvedTaskVersionId = resolvedTaskVersionId;
            state.ResolvedTaskVersion = resolvedVersion;
            state.StartedAt = startedAt;
            state.UpdatedAt = startedAt;
            return Task.FromResult<(bool, ProcessStepRecord?)>((true, state));
        }

        public Task CompleteProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            Dictionary<string, JsonElement> outputData,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            var state = States[taskInstanceId];
            state.Status = ProcessStepStatus.Completed;
            state.InputData = effectiveInputData;
            state.OutputData = outputData;
            state.CompletedAt = completedAt;
            state.FailedAt = null;
            state.ErrorMessage = null;
            state.UpdatedAt = completedAt;
            return Task.CompletedTask;
        }

        public Task FailProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            string? errorMessage,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken)
        {
            var state = States[taskInstanceId];
            state.Status = ProcessStepStatus.Failed;
            state.InputData = effectiveInputData;
            state.ErrorMessage = errorMessage;
            state.FailedAt = failedAt;
            state.UpdatedAt = failedAt;
            return Task.CompletedTask;
        }

        private static bool IsDispatchDue(
            ProcessStepDispatchState? dispatch,
            DateTimeOffset now) =>
            dispatch is null ||
            dispatch.Status is ProcessStepDispatchStatus.Pending ||
            dispatch.Status is ProcessStepDispatchStatus.RetryableDispatchError &&
                (!dispatch.NextAttemptAt.HasValue || dispatch.NextAttemptAt.Value <= now) ||
            dispatch.Status is ProcessStepDispatchStatus.Dispatching &&
                dispatch.LockedUntil.HasValue &&
                dispatch.LockedUntil.Value <= now;

        private bool TryGetLeasedDispatch(
            ProcessStepRecord state,
            string leaseId,
            out ProcessStepDispatchState dispatch)
        {
            dispatch = null!;
            if (!States.TryGetValue(Guid.Parse(state.Id), out var current) ||
                current.Dispatch is null ||
                !string.Equals(current.Dispatch.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return false;
            }

            dispatch = current.Dispatch;
            return true;
        }
    }

    private sealed class InMemoryProcessInstanceRepository
        : IProcessInstanceRepository<Dictionary<string, JsonElement>>
    {
        public InMemoryProcessInstanceRepository(ProcessInstanceRecord<Dictionary<string, JsonElement>> state)
        {
            State = state;
        }

        public ProcessInstanceRecord<Dictionary<string, JsonElement>> State { get; private set; }

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> CreateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessInstanceRecord<Dictionary<string, JsonElement>>> GetProcessStateById(
            Guid processId,
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(State);

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
            CancellationToken cancellationToken)
        {
            State = state;
            return Task.FromResult(state);
        }

        public Task<bool> TryUpdateProcessState(
            ProcessInstanceRecord<Dictionary<string, JsonElement>> state,
            CancellationToken cancellationToken)
        {
            State = state;
            return Task.FromResult(true);
        }
    }

    private sealed class InMemoryNodeJoinStateRepository : INodeJoinStateRepository
    {
        public Dictionary<(Guid ProcessId, Guid NodeId), NodeJoinStateRecord> States { get; } = new();

        public Task<NodeJoinStateRecord?> GetJoinStateAsync(
            Guid processId,
            Guid blueprintNodeId,
            CancellationToken cancellationToken)
        {
            States.TryGetValue((processId, blueprintNodeId), out var state);
            return Task.FromResult(state);
        }

        public Task<(bool Created, NodeJoinStateRecord State)> CreateJoinStateIfNotExistsAsync(
            NodeJoinStateRecord state,
            CancellationToken cancellationToken)
        {
            var key = (Guid.Parse(state.ProcessId), Guid.Parse(state.BlueprintNodeId));
            if (States.TryGetValue(key, out var existing))
            {
                return Task.FromResult((false, existing));
            }

            States[key] = state;
            return Task.FromResult((true, state));
        }

        public Task<bool> TryUpdateJoinStateAsync(
            NodeJoinStateRecord state,
            CancellationToken cancellationToken)
        {
            States[(Guid.Parse(state.ProcessId), Guid.Parse(state.BlueprintNodeId))] = state;
            return Task.FromResult(true);
        }
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

    private sealed class ProcessStepQueueMessageStub : IProcessStepQueueMessage
    {
        public ProcessStepQueueMessageStub(ProcessStepInitialisationMessage payload)
        {
            Payload = payload;
        }

        public string MessageId { get; } = Guid.NewGuid().ToString();
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LockedUntil { get; } = DateTimeOffset.UtcNow.AddMinutes(1);
        public ProcessStepInitialisationMessage Payload { get; }

        public Task CompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AbandonAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RenewLockAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
