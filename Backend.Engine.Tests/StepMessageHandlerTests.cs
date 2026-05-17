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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class StepMessageHandlerTests
{
    [Test]
    public async Task Missing_blueprint_node_abandons_message()
    {
        var node = CreatePluginNode(Guid.NewGuid());
        var blueprint = CreateBlueprint(node);
        var queueMessage = new ProcessStepQueueMessageSpy(new ProcessStepInitialisationMessage
        {
            ProcessId = Guid.NewGuid(),
            ProcessStepId = Guid.NewGuid(),
            ExecutableBlueprintId = blueprint.Id,
            BlueprintNodeId = Guid.NewGuid()
        });
        var stepExecution = new Mock<IStepExecutionService>();
        var handler = CreateHandler(
            blueprint,
            stepExecution.Object,
            Mock.Of<ICollectionIteratorExecutionService>(),
            Mock.Of<IStepFinalizationService>(),
            Mock.Of<IProcessStepFinalizationRepository>());

        await handler.HandleProcessStepMessageAsync(queueMessage, CancellationToken.None);

        queueMessage.AbandonCount.ShouldBe(1);
        stepExecution.Verify(
            service => service.StartPluginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Plugin_message_routes_to_step_execution_service()
    {
        var node = CreatePluginNode(Guid.NewGuid());
        var blueprint = CreateBlueprint(node);
        var queueMessage = CreateQueueMessage(blueprint, node);
        var finalizationRepository = new Mock<IProcessStepFinalizationRepository>();
        finalizationRepository
            .Setup(repository => repository.GetAsync(queueMessage.Payload.ProcessStepId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStepFinalizationRecord?)null);
        var stepExecution = new Mock<IStepExecutionService>();
        stepExecution
            .Setup(service => service.StartPluginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StepStartResult.Started);
        var collectionIterator = new Mock<ICollectionIteratorExecutionService>();
        var handler = CreateHandler(
            blueprint,
            stepExecution.Object,
            collectionIterator.Object,
            Mock.Of<IStepFinalizationService>(),
            finalizationRepository.Object);

        await handler.HandleProcessStepMessageAsync(queueMessage, CancellationToken.None);

        stepExecution.Verify(
            service => service.StartPluginStepAsync(
                It.Is<StepExecutionContext>(context => context.BlueprintNodeId == node.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        collectionIterator.Verify(
            service => service.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        queueMessage.AbandonCount.ShouldBe(0);
    }

    [Test]
    public async Task Collection_iterator_message_routes_to_native_execution_service()
    {
        var node = CreatePluginNode(Guid.NewGuid());
        node.NodeKind = ExecutableBlueprintNodeKind.CollectionIterator;
        var blueprint = CreateBlueprint(node);
        var queueMessage = CreateQueueMessage(blueprint, node);
        var finalizationRepository = new Mock<IProcessStepFinalizationRepository>();
        finalizationRepository
            .Setup(repository => repository.GetAsync(queueMessage.Payload.ProcessStepId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessStepFinalizationRecord?)null);
        var stepExecution = new Mock<IStepExecutionService>();
        var collectionIterator = new Mock<ICollectionIteratorExecutionService>();
        var handler = CreateHandler(
            blueprint,
            stepExecution.Object,
            collectionIterator.Object,
            Mock.Of<IStepFinalizationService>(),
            finalizationRepository.Object);

        await handler.HandleProcessStepMessageAsync(queueMessage, CancellationToken.None);

        collectionIterator.Verify(
            service => service.ExecuteAsync(
                It.Is<StepExecutionContext>(context => context.BlueprintNodeId == node.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        stepExecution.Verify(
            service => service.StartPluginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Incomplete_finalization_record_resumes_without_plugin_execution()
    {
        var node = CreatePluginNode(Guid.NewGuid());
        var blueprint = CreateBlueprint(node);
        var queueMessage = CreateQueueMessage(blueprint, node);
        var finalization = new ProcessStepFinalizationRecord
        {
            id = queueMessage.Payload.ProcessStepId.ToString(),
            ProcessId = queueMessage.Payload.ProcessId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            BlueprintNodeId = node.Id.ToString(),
            TaskDefinitionId = node.TaskDefinitionId,
            ResolvedTaskVersionId = node.ResolvedTaskVersionId,
            ResolvedTaskVersion = node.ResolvedVersion.ToString(),
            Outcome = ProcessStepFinalizationOutcome.Succeeded,
            Source = ProcessStepFinalizationSource.Plugin,
            Stage = ProcessStepFinalizationStage.OutputsPersisted,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var finalizationRepository = new Mock<IProcessStepFinalizationRepository>();
        finalizationRepository
            .Setup(repository => repository.GetAsync(queueMessage.Payload.ProcessStepId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(finalization);
        var finalizationService = new Mock<IStepFinalizationService>();
        var stepExecution = new Mock<IStepExecutionService>();
        var handler = CreateHandler(
            blueprint,
            stepExecution.Object,
            Mock.Of<ICollectionIteratorExecutionService>(),
            finalizationService.Object,
            finalizationRepository.Object);

        await handler.HandleProcessStepMessageAsync(queueMessage, CancellationToken.None);

        finalizationService.Verify(
            service => service.ResumeStepFinalizationAsync(
                It.IsAny<StepExecutionContext>(),
                finalization,
                It.IsAny<CancellationToken>()),
            Times.Once);
        stepExecution.Verify(
            service => service.StartPluginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static StepMessageHandler CreateHandler(
        ExecutableBlueprint blueprint,
        IStepExecutionService stepExecutionService,
        ICollectionIteratorExecutionService collectionIteratorExecutionService,
        IStepFinalizationService stepFinalizationService,
        IProcessStepFinalizationRepository finalizationRepository)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<StepExecutionContext>>(NullLogger<StepExecutionContext>.Instance);
        services.AddSingleton(finalizationRepository);
        services.AddSingleton(collectionIteratorExecutionService);
        services.AddSingleton(stepFinalizationService);
        var provider = services.BuildServiceProvider();

        return new StepMessageHandler(
            new BlueprintExecutionCacheStub(blueprint),
            new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance),
            provider.GetRequiredService<IServiceScopeFactory>(),
            stepExecutionService,
            NullLogger<StepMessageHandler>.Instance);
    }

    private static ProcessStepQueueMessageSpy CreateQueueMessage(
        ExecutableBlueprint blueprint,
        ExecutableBlueprintNode node) =>
        new(new ProcessStepInitialisationMessage
        {
            ProcessId = Guid.NewGuid(),
            ProcessStepId = Guid.NewGuid(),
            ExecutableBlueprintId = blueprint.Id,
            BlueprintNodeId = node.Id
        });

    private static ExecutableBlueprint CreateBlueprint(ExecutableBlueprintNode node) =>
        new()
        {
            Id = 101,
            DefinitionId = 10,
            VersionNumber = 1,
            Name = "Handler Blueprint",
            InitialNode = node,
            AllNodes = [node]
        };

    private static ExecutableBlueprintNode CreatePluginNode(Guid nodeId) =>
        new()
        {
            Id = nodeId,
            Name = "Plugin",
            TaskDefinitionId = 10,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedVersion = new Version(1, 0, 0),
            ResolvedTaskVersionId = 100,
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

    private sealed class BlueprintExecutionCacheStub(ExecutableBlueprint blueprint) : IBlueprintExecutionCache
    {
        public Task<ExecutableBlueprint> GetExecutableBlueprintAsync(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(blueprint);
    }

    private sealed class ProcessStepQueueMessageSpy(ProcessStepInitialisationMessage payload) : IProcessStepQueueMessage
    {
        public int CompleteCount { get; private set; }
        public int AbandonCount { get; private set; }
        public string MessageId { get; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow.AddSeconds(-5);
        public DateTimeOffset LockedUntil { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public ProcessStepInitialisationMessage Payload { get; } = payload;

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            CompleteCount++;
            return Task.CompletedTask;
        }

        public Task AbandonAsync(CancellationToken cancellationToken)
        {
            AbandonCount++;
            return Task.CompletedTask;
        }

        public Task RenewLockAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
