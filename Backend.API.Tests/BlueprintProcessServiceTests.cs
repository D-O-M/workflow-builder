using System;
using System.Collections.Generic;
using System.IO;
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
            queueWriter,
            repository,
            processInstances,
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
            queueWriter,
            repository,
            processInstances,
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
            queueWriter,
            repository,
            processInstances,
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
        processSteps.CreatedStep.InputData.ShouldNotBeNull();
        processSteps.CreatedStep.InputData.ShouldContainKey("Input");
        var inputPayload = processSteps.CreatedStep.InputData["Input"];
        inputPayload.GetProperty("town").GetString().ShouldBe("runtime");
        inputPayload.GetProperty("country").GetString().ShouldBe("uk");
        inputPayload.GetProperty("mode").GetString().ShouldBe("design-time");

        queueWriter.Messages.Count.ShouldBe(1);
        queueWriter.Messages[0].ProcessId.ShouldBe(accepted.ProcessId);
        queueWriter.Messages[0].ProcessStepId.ShouldBe(Guid.Parse(processSteps.CreatedStep.Id));
        queueWriter.Messages[0].BlueprintNodeId.ShouldBe(initialNodeId);
    }

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

        public Task<List<BlueprintViewModel>> GetAllBlueprints(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BlueprintViewModel> GetBlueprintById(int id, CancellationToken cancellationToken) =>
            Task.FromResult(_blueprint);

        public Task<BlueprintViewModel> CreateBlueprint(BlueprintInputModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BlueprintVersionViewModel> CreateVersion(BlueprintVersionInputModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessStepRepositorySpy : IProcessStepRepository
    {
        public ProcessStepRecord? CreatedStep { get; private set; }

        public Task<ProcessStepRecord> CreateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken)
        {
            CreatedStep = state;
            return Task.FromResult(state);
        }

        public Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> GetProcessStepById(Guid taskInstanceId, Guid processId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(Guid processId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> UpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessStep(ProcessStepRecord state, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
    }

    private sealed class ProcessStepQueueWriterSpy : IProcessStepQueueWriter
    {
        public List<ProcessStepInitialisationMessage> Messages { get; } = [];

        public Task EnqueueAsync(ProcessStepInitialisationMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
