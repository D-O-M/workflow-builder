using System;
using System.Collections.Generic;
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
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public class StepFinalizationGlobalStateTests
{
    [Test]
    public async Task FinalizeSucceededStepAsync_preserves_existing_global_state_when_plugin_returns_no_global_state_update()
    {
        var (context, service, processState, _) = CreateContext();

        await service.FinalizeSucceededStepAsync(
            context,
            CreateResult(context, succeeded: true, globalState: null),
            CancellationToken.None);

        processState.GlobalState.Keys.ShouldBe(["Existing"]);
        processState.GlobalState["Existing"].GetString().ShouldBe("kept");
    }

    [Test]
    public async Task FinalizeFailedStepAsync_preserves_existing_global_state_when_plugin_returns_no_global_state_update()
    {
        var (context, service, processState, _) = CreateContext();

        await service.FinalizeFailedStepAsync(
            context,
            CreateResult(context, succeeded: false, globalState: null),
            CancellationToken.None);

        processState.GlobalState.Keys.ShouldBe(["Existing"]);
        processState.GlobalState["Existing"].GetString().ShouldBe("kept");
    }

    [Test]
    public async Task FinalizeSucceededStepAsync_replaces_global_state_when_stateful_plugin_returns_global_state()
    {
        var (context, service, processState, _) = CreateContext();
        var globalState = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["Updated"] = JsonString("next")
            };

        await service.FinalizeSucceededStepAsync(
            context,
            CreateResult(context, succeeded: true, globalState),
            CancellationToken.None);

        processState.GlobalState.Keys.ShouldBe(["Updated"]);
        processState.GlobalState["Updated"].GetString().ShouldBe("next");
    }

    private static (
        StepExecutionContext Context,
        StepFinalizationService Service,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> ProcessState,
        ProcessStepRecord StepState) CreateContext()
    {
        var processId = Guid.NewGuid();
        var processStepId = Guid.NewGuid();
        var blueprintNodeId = Guid.NewGuid();
        const int executableBlueprintId = 42;

        var stepState = new ProcessStepRecord
        {
            id = processStepId.ToString(),
            BlueprintNodeId = blueprintNodeId.ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = executableBlueprintId.ToString(),
            Status = ProcessStepStatus.InProgress
        };

        var processState = new ProcessInstanceRecord<Dictionary<string, JsonElement>>
        {
            id = processId.ToString(),
            BlueprintId = executableBlueprintId.ToString(),
            Status = ProcessStatus.InProgress,
            ExecutionProgress = new ProcessExecutionProgress
            {
                TotalNodeCount = 1
            },
            GlobalState = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["Existing"] = JsonString("kept")
            }
        };

        var stepRepository = new Mock<IProcessStepRepository>();
        stepRepository
            .Setup(repository => repository.GetProcessStepById(processStepId, processId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stepState);
        stepRepository
            .Setup(repository => repository.UpdateProcessStep(stepState, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stepState);
        stepRepository
            .Setup(repository => repository.CompleteProcessStepAsync(
                processStepId,
                processId,
                It.IsAny<Dictionary<string, JsonElement>?>(),
                It.IsAny<Dictionary<string, JsonElement>>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, Dictionary<string, JsonElement>?, Dictionary<string, JsonElement>, DateTimeOffset, CancellationToken>(
                (_, _, effectiveInputData, outputData, completedAt, _) =>
                {
                    stepState.Status = ProcessStepStatus.Completed;
                    stepState.InputData = effectiveInputData;
                    stepState.OutputData = outputData;
                    stepState.CompletedAt = completedAt;
                    stepState.UpdatedAt = completedAt;
                })
            .Returns(Task.CompletedTask);
        stepRepository
            .Setup(repository => repository.FailProcessStepAsync(
                processStepId,
                processId,
                It.IsAny<Dictionary<string, JsonElement>?>(),
                It.IsAny<string?>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, Dictionary<string, JsonElement>?, string?, DateTimeOffset, CancellationToken>(
                (_, _, effectiveInputData, errorMessage, failedAt, _) =>
                {
                    stepState.Status = ProcessStepStatus.Failed;
                    stepState.InputData = effectiveInputData;
                    stepState.ErrorMessage = errorMessage;
                    stepState.FailedAt = failedAt;
                    stepState.UpdatedAt = failedAt;
                })
            .Returns(Task.CompletedTask);

        var processRepository = new Mock<IProcessInstanceRepository<Dictionary<string, JsonElement>>>();
        processRepository
            .Setup(repository => repository.GetProcessStateById(processId, executableBlueprintId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(processState);
        processRepository
            .Setup(repository => repository.UpdateProcessState(processState, It.IsAny<CancellationToken>()))
            .ReturnsAsync(processState);
        processRepository
            .Setup(repository => repository.TryUpdateProcessState(processState, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var queueMessage = new Mock<IProcessStepQueueMessage>();
        queueMessage.SetupGet(message => message.MessageId).Returns("message-1");
        queueMessage.SetupGet(message => message.LockedUntil).Returns(DateTimeOffset.UtcNow.AddMinutes(1));
        queueMessage
            .Setup(message => message.CompleteAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var blueprintNode = new ExecutableBlueprintNode
        {
            Id = blueprintNodeId,
            Name = "NoStatePlugin",
            TaskDefinitionId = 7,
            AllowedMajor = 0,
            AllowedMinor = 1,
            ResolvedVersion = new Version(0, 1, 0),
            ResolvedTaskVersionId = 700,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = "Input",
                Properties = new Dictionary<string, string>(StringComparer.Ordinal),
                OutboundBindings = []
            },
            OutputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal),
            InboundBindings = [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };

        var executableBlueprint = new ExecutableBlueprint
        {
            Id = executableBlueprintId,
            DefinitionId = 1,
            VersionNumber = 1,
            Name = "Test Blueprint",
            InitialNode = blueprintNode,
            AllNodes = [blueprintNode]
        };

        var context = new StepExecutionContext(
            new ProcessStepInitialisationMessage
            {
                ProcessId = processId,
                ProcessStepId = processStepId,
                ExecutableBlueprintId = executableBlueprintId,
                BlueprintNodeId = blueprintNodeId
            },
            NullLogger<StepExecutionContext>.Instance,
            queueMessage.Object,
            executableBlueprint);

        var telemetry = new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance);
        var finalizationRepository = new InMemoryProcessStepFinalizationRepository();
        var dispatchService = new ProcessStepDispatchService(
            stepRepository.Object,
            Mock.Of<IProcessStepQueueWriter>(),
            new ProcessStepDispatchOptions(),
            new ProcessStepDispatchTelemetry(),
            NullLogger<ProcessStepDispatchService>.Instance);
        var activationPersistenceService = new ActivationPersistenceService(
            Mock.Of<INodeJoinStateRepository>(),
            dispatchService,
            stepRepository.Object,
            NullLogger<ActivationPersistenceService>.Instance);
        var phasedProcessFinalizationService = new PhasedProcessFinalizationService(
            Mock.Of<IPhasedBlueprintRepository>(),
            Mock.Of<IPhasedProcessRepository>(),
            Mock.Of<IPhaseCorrelationRepository>(),
            processRepository.Object,
            stepRepository.Object,
            NullLogger<PhasedProcessFinalizationService>.Instance);
        var stepClaimService = new StepClaimService(
            stepRepository.Object,
            processRepository.Object,
            telemetry);
        var service = new StepFinalizationService(
            new ActivationPlanner(),
            activationPersistenceService,
            phasedProcessFinalizationService,
            finalizationRepository,
            stepRepository.Object,
            processRepository.Object,
            stepClaimService,
            telemetry,
            NullLogger<StepFinalizationService>.Instance);

        return (context, service, processState, stepState);
    }

    private static PluginExecutionResult CreateResult(
        StepExecutionContext context,
        bool succeeded,
        Dictionary<string, JsonElement>? globalState) =>
        new(
            context.ProcessStepId,
            succeeded,
            EffectiveInputData: null,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            GlobalState: globalState,
            StartedAt: DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            TimingsMs: null,
            ErrorMessage: succeeded ? null : "boom");

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse($"\"{value}\"");
        return document.RootElement.Clone();
    }

    private sealed class InMemoryProcessStepFinalizationRepository : IProcessStepFinalizationRepository
    {
        private readonly Dictionary<Guid, ProcessStepFinalizationRecord> _records = new();

        public Task<ProcessStepFinalizationRecord?> GetAsync(
            Guid processStepId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_records.GetValueOrDefault(processStepId));

        public Task<(bool Created, ProcessStepFinalizationRecord Record)> CreateIfNotExistsAsync(
            ProcessStepFinalizationRecord record,
            CancellationToken cancellationToken)
        {
            var id = Guid.Parse(record.Id);
            if (_records.TryGetValue(id, out var existing))
            {
                return Task.FromResult((false, existing));
            }

            _records[id] = record;
            return Task.FromResult((true, record));
        }

        public Task<bool> TryUpdateAsync(
            ProcessStepFinalizationRecord record,
            CancellationToken cancellationToken)
        {
            _records[Guid.Parse(record.Id)] = record;
            return Task.FromResult(true);
        }
    }
}
