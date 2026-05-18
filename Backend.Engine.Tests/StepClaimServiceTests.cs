using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class StepClaimServiceTests
{
    [Test]
    public async Task TryClaimStepAsync_hydrates_step_and_marks_pending_process_in_progress()
    {
        var fixture = CreateFixture(ProcessStatus.Pending, ProcessStepStatus.Pending);
        fixture.StepRepository
            .Setup(repository => repository.TryClaimProcessStepAsync(
                fixture.Context.ProcessStepId,
                fixture.Context.ProcessId,
                fixture.Context.TaskDefinitionId,
                fixture.Context.AllowedMajor,
                fixture.Context.AllowedMinor,
                fixture.Context.ResolvedTaskVersionId,
                fixture.Context.BlueprintNode.ResolvedVersion.ToString(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, fixture.Step));
        var service = CreateService(fixture);

        var result = await service.TryClaimStepAsync(fixture.Context, CancellationToken.None);

        result.StepClaimed.ShouldBeTrue();
        result.ProcessStarted.ShouldBeTrue();
        fixture.Context.InputHydrationPlan.ShouldBeSameAs(fixture.Step.InputHydrationPlan);
        fixture.Context.IterationContext.ShouldBe(fixture.Step.IterationContext);
        fixture.Process.Status.ShouldBe(ProcessStatus.InProgress);
        fixture.ProcessRepository.Verify(
            repository => repository.UpdateProcessState(fixture.Process, It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.ProcessIndexProjectionService.Verify(
            service => service.UpsertFromProcessAsync(
                fixture.Process,
                fixture.Context.BlueprintDefinitionId,
                fixture.Context.ExecutableBlueprintId,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task TryClaimStepAsync_returns_not_claimed_for_non_claimable_step()
    {
        var fixture = CreateFixture(ProcessStatus.InProgress, ProcessStepStatus.Completed);
        fixture.StepRepository
            .Setup(repository => repository.TryClaimProcessStepAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, (ProcessStepRecord?)null));
        var service = CreateService(fixture);

        var result = await service.TryClaimStepAsync(fixture.Context, CancellationToken.None);

        result.StepClaimed.ShouldBeFalse();
        result.ProcessStarted.ShouldBeFalse();
    }

    [Test]
    public async Task ResetStepToPendingAsync_resets_only_in_progress_steps()
    {
        var fixture = CreateFixture(ProcessStatus.InProgress, ProcessStepStatus.InProgress);
        var service = CreateService(fixture);

        await service.ResetStepToPendingAsync(fixture.Context, CancellationToken.None);

        fixture.Step.Status.ShouldBe(ProcessStepStatus.Pending);
        fixture.Step.StartedAt.ShouldBeNull();
        fixture.StepRepository.Verify(
            repository => repository.UpdateProcessStep(fixture.Step, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static StepClaimService CreateService(Fixture fixture) =>
        new(
            fixture.StepRepository.Object,
            fixture.ProcessRepository.Object,
            new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance),
            fixture.ProcessIndexProjectionService.Object,
            NullLogger<StepClaimService>.Instance);

    private static Fixture CreateFixture(
        ProcessStatus processStatus,
        ProcessStepStatus stepStatus)
    {
        var processId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var blueprint = CreateBlueprint(nodeId);
        var message = new ProcessStepInitialisationMessage
        {
            ProcessId = processId,
            ProcessStepId = stepId,
            ExecutableBlueprintId = blueprint.Id,
            BlueprintNodeId = nodeId
        };
        var queueMessage = new Mock<IProcessStepQueueMessage>();
        queueMessage.SetupGet(message => message.MessageId).Returns("message-1");
        queueMessage.SetupGet(message => message.LockedUntil).Returns(DateTimeOffset.UtcNow.AddMinutes(5));
        var context = new StepExecutionContext(
            message,
            NullLogger<StepExecutionContext>.Instance,
            queueMessage.Object,
            blueprint);
        var step = new ProcessStepRecord
        {
            Id = stepId.ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            BlueprintNodeId = nodeId.ToString(),
            Status = stepStatus,
            StartedAt = DateTimeOffset.UtcNow,
            InputHydrationPlan = new InputHydrationPlan
            {
                InputChannelName = "Input"
            },
            IterationContext = new ProcessStepIterationContext
            {
                SourceIteratorStepId = Guid.NewGuid().ToString(),
                SourceIteratorBlueprintNodeId = Guid.NewGuid().ToString(),
                ItemIndex = 2,
                ItemKey = "item-2"
            }
        };
        var process = new ProcessInstanceRecord<Dictionary<string, JsonElement>>
        {
            id = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            Status = processStatus,
            GlobalState = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            NodeOverrides = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal)
        };
        var stepRepository = new Mock<IProcessStepRepository>();
        stepRepository
            .Setup(repository => repository.GetProcessStepById(stepId, processId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        stepRepository
            .Setup(repository => repository.UpdateProcessStep(step, It.IsAny<CancellationToken>()))
            .ReturnsAsync(step);
        var processRepository = new Mock<IProcessInstanceRepository<Dictionary<string, JsonElement>>>();
        processRepository
            .Setup(repository => repository.GetProcessStateById(processId, blueprint.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(process);
        processRepository
            .Setup(repository => repository.UpdateProcessState(process, It.IsAny<CancellationToken>()))
            .ReturnsAsync(process);

        var processIndexProjectionService = new Mock<IProcessIndexProjectionService>();
        processIndexProjectionService
            .Setup(service => service.UpsertFromProcessAsync(
                It.IsAny<ProcessInstanceRecord<Dictionary<string, JsonElement>>>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new Fixture(context, step, process, stepRepository, processRepository, processIndexProjectionService);
    }

    private static ExecutableBlueprint CreateBlueprint(Guid nodeId)
    {
        var node = new ExecutableBlueprintNode
        {
            Id = nodeId,
            Name = "Plugin",
            TaskDefinitionId = 10,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedTaskVersionId = 100,
            ResolvedVersion = new Version(1, 0, 0),
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

        return new ExecutableBlueprint
        {
            Id = 101,
            DefinitionId = 10,
            VersionNumber = 1,
            Name = "Claim Blueprint",
            InitialNode = node,
            AllNodes = [node]
        };
    }

    private sealed record Fixture(
        StepExecutionContext Context,
        ProcessStepRecord Step,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> Process,
        Mock<IProcessStepRepository> StepRepository,
        Mock<IProcessInstanceRepository<Dictionary<string, JsonElement>>> ProcessRepository,
        Mock<IProcessIndexProjectionService> ProcessIndexProjectionService);
}
