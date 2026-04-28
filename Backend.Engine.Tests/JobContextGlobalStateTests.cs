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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public class JobContextGlobalStateTests
{
    [Test]
    public async Task PersistSuccessAsync_Preserves_existing_global_state_when_plugin_returns_no_global_state_update()
    {
        var (context, processState, _) = CreateContext();

        await context.PersistSuccessAsync(
            effectiveInputData: null,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            globalState: null,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        processState.GlobalState.Keys.ShouldBe(["Existing"]);
        processState.GlobalState["Existing"].GetString().ShouldBe("kept");
    }

    [Test]
    public async Task PersistFailureAsync_Preserves_existing_global_state_when_plugin_returns_no_global_state_update()
    {
        var (context, processState, _) = CreateContext();

        await context.PersistFailureAsync(
            effectiveInputData: null,
            globalState: null,
            errorMessage: "boom",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        processState.GlobalState.Keys.ShouldBe(["Existing"]);
        processState.GlobalState["Existing"].GetString().ShouldBe("kept");
    }

    [Test]
    public async Task PersistSuccessAsync_Replaces_global_state_when_stateful_plugin_returns_global_state()
    {
        var (context, processState, _) = CreateContext();

        await context.PersistSuccessAsync(
            effectiveInputData: null,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["Updated"] = JsonString("next")
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        processState.GlobalState.Keys.ShouldBe(["Updated"]);
        processState.GlobalState["Updated"].GetString().ShouldBe("next");
    }

    private static (
        JobContext Context,
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

        var context = new JobContext(
            new ProcessStepInitialisationMessage
            {
                ProcessId = processId,
                ProcessStepId = processStepId,
                ExecutableBlueprintId = executableBlueprintId,
                BlueprintNodeId = blueprintNodeId
            },
            NullLogger<JobContext>.Instance,
            queueMessage.Object,
            stepRepository.Object,
            processRepository.Object,
            executableBlueprint);

        return (context, processState, stepState);
    }

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse($"\"{value}\"");
        return document.RootElement.Clone();
    }
}
