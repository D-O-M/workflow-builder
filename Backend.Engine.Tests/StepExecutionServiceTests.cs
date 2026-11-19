using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Engine.Debugging;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.PluginAbi;
using Backend.Plugins.Communication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class StepExecutionServiceTests
{
    [Test]
    public async Task StartPluginStepAsync_reloads_catalog_once_before_deferring_unresolved_plugin()
    {
        var catalog = new PluginRuntimeCatalog();
        var reloader = new Mock<IPluginRuntimeReloader>();
        var agentManager = new Mock<IAgentManager>();
        var fixture = CreateFixture();
        var service = CreateService(catalog, reloader.Object, agentManager.Object, fixture);

        var result = await service.StartPluginStepAsync(fixture.Context, CancellationToken.None);

        result.Disposition.ShouldBe(StepStartDisposition.Deferred);
        reloader.Verify(loader => loader.ReloadAsync(It.IsAny<CancellationToken>()), Times.Once);
        agentManager.Verify(manager => manager.TryAcquireStepSlot(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task Disabled_lane_parks_before_settlement_and_tolerates_duplicate_delivery()
    {
        var catalog = new PluginRuntimeCatalog();
        var route = CreateTaskVersion();
        catalog.ReplaceRoutes([route], new Dictionary<PluginRuntimeLane, TaskDefinitionInfo>(),
            [new PluginRuntimeLane(route.TaskDefinitionId, route.Version.Major, route.Version.Minor)], 7);
        var fixture = CreateFixture();
        ProcessStepPluginWait? durableWait = null;
        var writes = 0;
        fixture.StepRepository.Setup(repo => repo.GetProcessStepById(
                fixture.Context.ProcessStepId, fixture.Context.ProcessId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ProcessStepRecord { Status = ProcessStepStatus.Pending, PluginWait = durableWait });
        fixture.StepRepository.Setup(repo => repo.TryUpdateProcessStep(It.IsAny<ProcessStepRecord>(), It.IsAny<CancellationToken>()))
            .Returns<ProcessStepRecord, CancellationToken>((step, _) =>
            {
                if (++writes == 1) throw new InvalidOperationException("Storage write interrupted");
                fixture.QueueMessage.CompleteCount.ShouldBe(0);
                durableWait = step.PluginWait;
                return Task.FromResult(true);
            });
        var agents = new Mock<IAgentManager>();
        var service = CreateService(catalog, Mock.Of<IPluginRuntimeReloader>(), agents.Object, fixture);

        await Should.ThrowAsync<InvalidOperationException>(() => service.StartPluginStepAsync(fixture.Context, CancellationToken.None));
        fixture.QueueMessage.CompleteCount.ShouldBe(0);
        await service.StartPluginStepAsync(fixture.Context, CancellationToken.None);
        await service.StartPluginStepAsync(fixture.Context, CancellationToken.None);

        durableWait!.PolicyRevision.ShouldBe(7);
        durableWait.TaskDefinitionId.ShouldBe(route.TaskDefinitionId);
        writes.ShouldBe(2);
        fixture.QueueMessage.CompleteCount.ShouldBe(2);
        agents.Verify(manager => manager.TryAcquireStepSlot(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task StartPluginStepAsync_uses_catalog_route_after_reload()
    {
        var catalog = new PluginRuntimeCatalog();
        var route = CreateTaskVersion();
        var reloader = new Mock<IPluginRuntimeReloader>();
        reloader
            .Setup(loader => loader.ReloadAsync(It.IsAny<CancellationToken>()))
            .Callback(() => catalog.ReplaceRoutes(
                [route],
                new Dictionary<PluginRuntimeLane, TaskDefinitionInfo>
                {
                    [new PluginRuntimeLane(route.TaskDefinitionId, route.Version.Major, route.Version.Minor)] = route
                }))
            .Returns(Task.CompletedTask);
        var agentManager = new Mock<IAgentManager>();
        agentManager
            .Setup(manager => manager.TryAcquireStepSlot(route.Id))
            .Returns(false);
        var fixture = CreateFixture();
        var service = CreateService(catalog, reloader.Object, agentManager.Object, fixture);

        var result = await service.StartPluginStepAsync(fixture.Context, CancellationToken.None);

        result.Disposition.ShouldBe(StepStartDisposition.Deferred);
        fixture.Context.ResolvedTaskVersionId.ShouldBe(route.Id);
        reloader.Verify(loader => loader.ReloadAsync(It.IsAny<CancellationToken>()), Times.Once);
        agentManager.Verify(manager => manager.TryAcquireStepSlot(route.Id), Times.Once);
    }

    [Test]
    public async Task StartPluginStepAsync_releases_agent_slot_and_completes_message_when_step_is_already_settled()
    {
        var route = CreateTaskVersion();
        var catalog = new PluginRuntimeCatalog();
        catalog.ReplaceRoutes(
            [route],
            new Dictionary<PluginRuntimeLane, TaskDefinitionInfo>
            {
                [new PluginRuntimeLane(route.TaskDefinitionId, route.Version.Major, route.Version.Minor)] = route
            });
        var reloader = new Mock<IPluginRuntimeReloader>();
        var agentManager = new Mock<IAgentManager>();
        agentManager
            .Setup(manager => manager.TryAcquireStepSlot(route.Id))
            .Returns(true);
        var fixture = CreateFixture();
        fixture.StepRepository
            .Setup(repository => repository.TryClaimGenerationAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(),
                0,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((false, (ProcessStepRecord?)null));
        var service = CreateService(catalog, reloader.Object, agentManager.Object, fixture);

        var result = await service.StartPluginStepAsync(fixture.Context, CancellationToken.None);

        result.Disposition.ShouldBe(StepStartDisposition.AlreadySettled);
        fixture.QueueMessage.CompleteCount.ShouldBe(1);
        agentManager.Verify(manager => manager.ReleaseStepSlot(route.Id), Times.Once);
        agentManager.Verify(manager => manager.GetAgent(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task HandlePluginResultAsync_ignores_unknown_step_result()
    {
        var fixture = CreateFixture();
        var service = CreateService(
            new PluginRuntimeCatalog(),
            Mock.Of<IPluginRuntimeReloader>(),
            Mock.Of<IAgentManager>(),
            fixture);

        await service.HandlePluginResultAsync(
            new PluginExecutionResult(
                Guid.NewGuid(),
                Succeeded: true,
                EffectiveInputData: null,
                OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: DateTimeOffset.UtcNow,
                TimingsMs: null,
                ErrorMessage: null),
            CancellationToken.None);
    }

    [Test]
    public async Task HandlePluginAgentExitedAsync_resets_active_step_abandons_message_and_releases_slot()
    {
        var fixture = CreateFixture();
        var agentManager = new Mock<IAgentManager>();
        var telemetry = new PluginRuntimeAgentTelemetry();
        var service = CreateService(
            new PluginRuntimeCatalog(),
            Mock.Of<IPluginRuntimeReloader>(),
            agentManager.Object,
            fixture,
            telemetry);
        var renewal = new LockRenewalHandleSpy();
        fixture.Context.AttachLockRenewal(renewal);
        TrackActiveStep(service, fixture.Context);
        SetupPersistedStep(fixture, ProcessStepStatus.InProgress);
        fixture.StepRepository
            .Setup(repository => repository.TryResetProcessStepToPendingAsync(
                fixture.Context.ProcessStepId,
                fixture.Context.ProcessId,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await service.HandlePluginAgentExitedAsync(
            CreateExitNotification(fixture.Context.ProcessStepId),
            CancellationToken.None);

        fixture.QueueMessage.AbandonCount.ShouldBe(1);
        fixture.QueueMessage.CompleteCount.ShouldBe(0);
        renewal.DisposeCount.ShouldBe(1);
        ActiveStepCount(service).ShouldBe(0);
        telemetry.RecordedActiveStepCleanupSucceeded.ShouldBe(1);
        telemetry.RecordedActiveStepCleanupFailed.ShouldBe(0);
        agentManager.Verify(
            manager => manager.ReleaseStepSlot(fixture.Context.ResolvedTaskVersionId),
            Times.Once);
    }

    [Test]
    public async Task HandlePluginAgentExitedAsync_cleanup_failure_does_not_settle_message()
    {
        var fixture = CreateFixture();
        var agentManager = new Mock<IAgentManager>();
        var telemetry = new PluginRuntimeAgentTelemetry();
        var service = CreateService(
            new PluginRuntimeCatalog(),
            Mock.Of<IPluginRuntimeReloader>(),
            agentManager.Object,
            fixture,
            telemetry);
        var renewal = new LockRenewalHandleSpy();
        fixture.Context.AttachLockRenewal(renewal);
        TrackActiveStep(service, fixture.Context);
        SetupPersistedStep(fixture, ProcessStepStatus.InProgress);
        fixture.StepRepository
            .Setup(repository => repository.TryResetProcessStepToPendingAsync(
                fixture.Context.ProcessStepId,
                fixture.Context.ProcessId,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await service.HandlePluginAgentExitedAsync(
            CreateExitNotification(fixture.Context.ProcessStepId),
            CancellationToken.None);

        fixture.QueueMessage.AbandonCount.ShouldBe(0);
        fixture.QueueMessage.CompleteCount.ShouldBe(0);
        renewal.DisposeCount.ShouldBe(1);
        ActiveStepCount(service).ShouldBe(0);
        telemetry.RecordedActiveStepCleanupSucceeded.ShouldBe(0);
        telemetry.RecordedActiveStepCleanupFailed.ShouldBe(1);
        agentManager.Verify(
            manager => manager.ReleaseStepSlot(fixture.Context.ResolvedTaskVersionId),
            Times.Once);
    }

    [Test]
    public async Task HandlePluginAgentExitedAsync_tolerates_unknown_job_ids()
    {
        var fixture = CreateFixture();
        var agentManager = new Mock<IAgentManager>();
        var telemetry = new PluginRuntimeAgentTelemetry();
        var service = CreateService(
            new PluginRuntimeCatalog(),
            Mock.Of<IPluginRuntimeReloader>(),
            agentManager.Object,
            fixture,
            telemetry);

        await service.HandlePluginAgentExitedAsync(
            CreateExitNotification(Guid.NewGuid()),
            CancellationToken.None);

        fixture.QueueMessage.AbandonCount.ShouldBe(0);
        telemetry.RecordedActiveStepCleanupSucceeded.ShouldBe(0);
        telemetry.RecordedActiveStepCleanupFailed.ShouldBe(0);
        agentManager.Verify(
            manager => manager.ReleaseStepSlot(It.IsAny<int>()),
            Times.Never);
    }

    [Test]
    public async Task Health_retry_decision_resets_active_step_abandons_message_and_releases_slot()
    {
        var fixture = CreateFixture();
        var agentManager = new Mock<IAgentManager>();
        var telemetry = new PluginRuntimeAgentTelemetry();
        var service = CreateService(
            new PluginRuntimeCatalog(),
            Mock.Of<IPluginRuntimeReloader>(),
            agentManager.Object,
            fixture,
            telemetry);
        TrackActiveStep(service, fixture.Context);
        SetupPersistedStep(fixture, ProcessStepStatus.InProgress);
        fixture.StepRepository
            .Setup(repository => repository.TryResetProcessStepToPendingAsync(
                fixture.Context.ProcessStepId,
                fixture.Context.ProcessId,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var decision = new PluginAgentHealthPolicyDecision(
            PluginAgentHealthEvent.Create(
                new PluginAgentIdentity(
                    Guid.NewGuid(),
                    fixture.Context.ResolvedTaskVersionId,
                    fixture.Context.TaskDefinitionId,
                    "TestPlugin",
                    new Version(1, 0, 0)),
                PluginAgentHealthEventType.FinalizationQueueClosed,
                [fixture.Context.ProcessStepId]),
            PluginAgentHealthPolicyOutcome.Warn | PluginAgentHealthPolicyOutcome.RetryActiveStep,
            "retry",
            BackoffUntil: null,
            RepeatedCrashThresholdCrossed: false);

        await service.ApplyPluginAgentHealthDecisionAsync(decision, CancellationToken.None);

        fixture.QueueMessage.AbandonCount.ShouldBe(1);
        ActiveStepCount(service).ShouldBe(0);
        telemetry.RecordedActiveStepCleanupSucceeded.ShouldBe(1);
        agentManager.Verify(
            manager => manager.ReleaseStepSlot(fixture.Context.ResolvedTaskVersionId),
            Times.Once);
    }

    [Test]
    public async Task HandlePluginAgentExitedAsync_terminal_step_is_not_reset_destructively()
    {
        var fixture = CreateFixture();
        var agentManager = new Mock<IAgentManager>();
        var telemetry = new PluginRuntimeAgentTelemetry();
        var service = CreateService(
            new PluginRuntimeCatalog(),
            Mock.Of<IPluginRuntimeReloader>(),
            agentManager.Object,
            fixture,
            telemetry);
        TrackActiveStep(service, fixture.Context);
        SetupPersistedStep(fixture, ProcessStepStatus.Completed);

        await service.HandlePluginAgentExitedAsync(
            CreateExitNotification(fixture.Context.ProcessStepId),
            CancellationToken.None);

        fixture.QueueMessage.AbandonCount.ShouldBe(0);
        fixture.QueueMessage.CompleteCount.ShouldBe(0);
        telemetry.RecordedActiveStepCleanupSucceeded.ShouldBe(1);
        fixture.StepRepository.Verify(
            repository => repository.TryResetProcessStepToPendingAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        agentManager.Verify(
            manager => manager.ReleaseStepSlot(fixture.Context.ResolvedTaskVersionId),
            Times.Once);
    }

    private static StepExecutionService CreateService(
        PluginRuntimeCatalog catalog,
        IPluginRuntimeReloader reloader,
        IAgentManager agentManager,
        Fixture fixture,
        PluginRuntimeAgentTelemetry? pluginRuntimeAgentTelemetry = null)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var telemetry = new ProcessLifecycleTelemetry(NullLogger<ProcessLifecycleTelemetry>.Instance);
        var stepClaimService = new StepClaimService(
            fixture.StepRepository.Object,
            fixture.ProcessRepository.Object,
            telemetry);
        return new StepExecutionService(
            agentManager,
            catalog,
            reloader,
            telemetry,
            pluginRuntimeAgentTelemetry ?? new PluginRuntimeAgentTelemetry(),
            CreateLockRenewalService(new ProcessStepQueueLockRenewalOptions
            {
                ActiveMessageLockRenewalEnabled = false
            }),
            provider.GetRequiredService<IServiceScopeFactory>(),
            stepClaimService,
            NullLogger<StepExecutionService>.Instance);
    }

    private static ActiveStepLockRenewalService CreateLockRenewalService(
        ProcessStepQueueLockRenewalOptions? options = null,
        IAsyncDelay? delay = null,
        ProcessStepLockRenewalTelemetry? telemetry = null,
        IHostApplicationLifetime? applicationLifetime = null) =>
        new(
            options ?? new ProcessStepQueueLockRenewalOptions(),
            delay ?? new CompletedDelay(),
            telemetry ?? new ProcessStepLockRenewalTelemetry(),
            applicationLifetime ?? new ApplicationLifetimeStub(),
            NullLogger<ActiveStepLockRenewalService>.Instance);

    private static Fixture CreateFixture()
    {
        var processId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
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
        var blueprint = new ExecutableBlueprint
        {
            Id = 101,
            DefinitionId = 10,
            VersionNumber = 1,
            Name = "Execution Blueprint",
            InitialNode = node,
            AllNodes = [node]
        };
        var message = new ProcessStepInitialisationMessage
        {
            ProcessId = processId,
            ProcessStepId = stepId,
            ExecutableBlueprintId = blueprint.Id,
            BlueprintNodeId = nodeId
        };
        var queueMessage = new ProcessStepQueueMessageSpy(message);
        var context = new StepExecutionContext(
            message,
            NullLogger<StepExecutionContext>.Instance,
            queueMessage,
            blueprint);
        var stepRepository = new Mock<IProcessStepRepository>();
        var processRepository = new Mock<IProcessInstanceRepository>();
        var fixture = new Fixture(context, queueMessage, stepRepository, processRepository);
        SetupPersistedStep(fixture, ProcessStepStatus.Pending);
        return fixture;
    }

    private static void SetupPersistedStep(
        Fixture fixture,
        ProcessStepStatus status)
    {
        fixture.StepRepository
            .Setup(repository => repository.GetProcessStepById(
                fixture.Context.ProcessStepId,
                fixture.Context.ProcessId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessStepRecord
            {
                Id = fixture.Context.ProcessStepId.ToString(),
                ProcessId = fixture.Context.ProcessId.ToString(),
                BlueprintId = fixture.Context.ExecutableBlueprintId.ToString(),
                BlueprintNodeId = fixture.Context.BlueprintNodeId.ToString(),
                Status = status
            });
    }

    private static PluginAgentExitNotification CreateExitNotification(Guid processStepId) =>
        new(
            new PluginAgentIdentity(
                Guid.NewGuid(),
                TaskVersionId: 100,
                TaskDefinitionId: 10,
                "TestPlugin",
                new Version(1, 0, 0)),
            ExitCode: 1,
            ShutdownRequested: false,
            ExitedBeforeReady: false,
            PolicyRequestedStop: false,
            [processStepId]);

    private static void TrackActiveStep(
        StepExecutionService service,
        StepExecutionContext context)
    {
        GetActiveSteps(service)
            .TryAdd(context.Id, context)
            .ShouldBeTrue();
    }

    private static int ActiveStepCount(StepExecutionService service) =>
        GetActiveSteps(service).Count;

    private static ConcurrentDictionary<Guid, StepExecutionContext> GetActiveSteps(StepExecutionService service)
    {
        var field = typeof(StepExecutionService).GetField(
            "_activeSteps",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.ShouldNotBeNull();
        return (ConcurrentDictionary<Guid, StepExecutionContext>)field.GetValue(service)!;
    }

    private static TaskDefinitionInfo CreateTaskVersion() =>
        new(
            100,
            10,
            "Plugin",
            "plugin.exe",
            new Version(1, 0, 0),
            TaskVersionState.Active,
            PluginAbiDefaults.CurrentMetadata);

    private sealed record Fixture(
        StepExecutionContext Context,
        ProcessStepQueueMessageSpy QueueMessage,
        Mock<IProcessStepRepository> StepRepository,
        Mock<IProcessInstanceRepository> ProcessRepository);

    private sealed class ProcessStepQueueMessageSpy(ProcessStepInitialisationMessage payload) : IProcessStepQueueMessage
    {
        public int CompleteCount { get; private set; }
        public string MessageId { get; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow.AddSeconds(-5);
        public DateTimeOffset LockedUntil { get; } = DateTimeOffset.UtcNow.AddMinutes(5);
        public ProcessStepInitialisationMessage Payload { get; } = payload;

        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            CompleteCount++;
            return Task.CompletedTask;
        }

        public int AbandonCount { get; private set; }

        public Task AbandonAsync(CancellationToken cancellationToken)
        {
            AbandonCount++;
            return Task.CompletedTask;
        }

        public Task<DateTimeOffset> RenewLockAsync(CancellationToken cancellationToken) =>
            Task.FromResult(LockedUntil);
    }

    private sealed class CompletedDelay : IAsyncDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ApplicationLifetimeStub : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class LockRenewalHandleSpy : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
