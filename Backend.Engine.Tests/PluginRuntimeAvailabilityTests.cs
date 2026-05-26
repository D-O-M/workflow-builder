using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Engine.Capabilities;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Models;
using Backend.PluginAbi;
using Backend.Services.PluginIngestion;
using Backend.Services.PluginRuntimeStatus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginRuntimeAvailabilityTests
{
    [Test]
    public async Task ReloadAsync_records_abi_incompatible_runtime_status()
    {
        var runtimeStore = new InMemoryPluginTaskVersionRuntimeStatusStore();
        var taskVersion = CreateTaskVersion(
            101,
            new Version(1, 0, 1),
            PluginAbiDefaults.CurrentMetadata with { ProcessProtocolVersion = 0 });
        var orchestrator = CreateOrchestrator([taskVersion], runtimeStore: runtimeStore);

        await orchestrator.ReloadAsync(CancellationToken.None);

        var record = (await runtimeStore.GetFreshByTaskVersionIdsAsync(
            [taskVersion.Id],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None)).Single();
        record.CurrentStatus.ShouldBe(PluginTaskVersionRuntimeStatus.UnrunnableAbiIncompatible);
    }

    [Test]
    public async Task ReloadAsync_records_missing_capability_runtime_status()
    {
        var runtimeStore = new InMemoryPluginTaskVersionRuntimeStatusStore();
        var taskVersion = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var capabilityService = CapabilityServiceReturning(new CapabilityValidationResult
        {
            CapabilityId = "software:command:git",
            DisplayName = "Git",
            Kind = CapabilityKindNames.Software,
            Required = true,
            Status = CapabilityValidationStatus.Missing,
            FailureCategory = CapabilityValidationFailureCategory.MissingCapability,
            Message = "git missing"
        });
        var orchestrator = CreateOrchestrator(
            [taskVersion],
            runtimeStore: runtimeStore,
            capabilityService: capabilityService,
            workspaceFactory: WorkspaceFactoryReturningTempDirectory(taskVersion));

        await orchestrator.ReloadAsync(CancellationToken.None);

        var record = (await runtimeStore.GetFreshByTaskVersionIdsAsync(
            [taskVersion.Id],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None)).Single();
        record.CurrentStatus.ShouldBe(PluginTaskVersionRuntimeStatus.UnrunnableMissingCapability);
        record.Issues.Single().CapabilityId.ShouldBe("software:command:git");
    }

    [Test]
    public async Task ReloadAsync_records_missing_configuration_runtime_status()
    {
        var runtimeStore = new InMemoryPluginTaskVersionRuntimeStatusStore();
        var taskVersion = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var capabilityService = CapabilityServiceReturning(new CapabilityValidationResult
        {
            CapabilityId = "configuration:test",
            DisplayName = "Test config",
            Kind = CapabilityKindNames.Configuration,
            Required = true,
            Status = CapabilityValidationStatus.Missing,
            FailureCategory = CapabilityValidationFailureCategory.MissingConfiguration,
            Message = "config missing"
        });
        var orchestrator = CreateOrchestrator(
            [taskVersion],
            runtimeStore: runtimeStore,
            capabilityService: capabilityService,
            workspaceFactory: WorkspaceFactoryReturningTempDirectory(taskVersion));

        await orchestrator.ReloadAsync(CancellationToken.None);

        var record = (await runtimeStore.GetFreshByTaskVersionIdsAsync(
            [taskVersion.Id],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None)).Single();
        record.CurrentStatus.ShouldBe(PluginTaskVersionRuntimeStatus.UnrunnableMissingConfiguration);
    }

    [Test]
    public async Task ReloadAsync_records_runtime_load_failed_for_download_failure()
    {
        var runtimeStore = new InMemoryPluginTaskVersionRuntimeStatusStore();
        var taskVersion = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var workspaceFactory = new Mock<IPluginWorkspaceFactoryUtility>();
        workspaceFactory
            .Setup(factory => factory.CreateWorkspaceAsync(taskVersion.Name, taskVersion.Version, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(@"Could not create C:\temp\workflow\plugin AccountKey=abc123;"));
        var orchestrator = CreateOrchestrator(
            [taskVersion],
            runtimeStore: runtimeStore,
            workspaceFactory: workspaceFactory.Object);

        await orchestrator.ReloadAsync(CancellationToken.None);

        var record = (await runtimeStore.GetFreshByTaskVersionIdsAsync(
            [taskVersion.Id],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None)).Single();
        record.CurrentStatus.ShouldBe(PluginTaskVersionRuntimeStatus.RuntimeLoadFailed);
        var message = record.Message.ShouldNotBeNull();
        message.ShouldNotContain(@"C:\temp\workflow");
        message.ShouldNotContain("abc123");
    }

    [Test]
    public async Task ReloadAsync_records_runnable_for_existing_agent()
    {
        var runtimeStore = new InMemoryPluginTaskVersionRuntimeStatusStore();
        var taskVersion = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var agentManager = new Mock<IAgentManager>();
        agentManager
            .Setup(manager => manager.AgentExists(taskVersion.Id))
            .Returns(true);
        var orchestrator = CreateOrchestrator(
            [taskVersion],
            agentManager: agentManager.Object,
            runtimeStore: runtimeStore);

        await orchestrator.ReloadAsync(CancellationToken.None);

        var record = (await runtimeStore.GetFreshByTaskVersionIdsAsync(
            [taskVersion.Id],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None)).Single();
        record.CurrentStatus.ShouldBe(PluginTaskVersionRuntimeStatus.Runnable);
    }

    [Test]
    public async Task ReloadAsync_records_unknown_not_loaded_for_stale_idle_candidate()
    {
        var runtimeStore = new InMemoryPluginTaskVersionRuntimeStatusStore();
        var stale = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var routed = CreateTaskVersion(102, new Version(1, 0, 2), PluginAbiDefaults.CurrentMetadata);
        var agentManager = new Mock<IAgentManager>();
        agentManager
            .Setup(manager => manager.AgentExists(stale.Id))
            .Returns(true);
        agentManager
            .Setup(manager => manager.AgentExists(routed.Id))
            .Returns(true);
        var orchestrator = CreateOrchestrator(
            [routed, stale],
            agentManager: agentManager.Object,
            runtimeStore: runtimeStore);

        await orchestrator.ReloadAsync(CancellationToken.None);

        var records = await runtimeStore.GetFreshByTaskVersionIdsAsync(
            [stale.Id, routed.Id],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None);
        records.Single(record => record.TaskVersionId == routed.Id)
            .CurrentStatus.ShouldBe(PluginTaskVersionRuntimeStatus.Runnable);
        records.Single(record => record.TaskVersionId == stale.Id)
            .CurrentStatus.ShouldBe(PluginTaskVersionRuntimeStatus.UnknownNotLoaded);
    }

    [Test]
    public async Task ReloadAsync_skips_runtime_quarantined_task_version()
    {
        var runtimeStore = new InMemoryPluginTaskVersionRuntimeStatusStore();
        var taskVersion = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var policy = new PluginAgentHealthPolicy(
            new PluginAgentHealthPolicyOptions
            {
                MaxCrashesPerWindow = 1
            },
            new PluginAgentHealthTelemetry());
        policy.Evaluate(
            PluginAgentHealthEvent.Create(
                PluginAgentIdentity.FromTaskVersion(taskVersion),
                PluginAgentHealthEventType.RuntimeLoadFailed,
                message: "startup failed"));
        var agentManager = new Mock<IAgentManager>();
        var orchestrator = CreateOrchestrator(
            [taskVersion],
            agentManager: agentManager.Object,
            runtimeStore: runtimeStore,
            healthPolicy: policy);

        await orchestrator.ReloadAsync(CancellationToken.None);

        agentManager.Verify(manager => manager.RegisterAgent(It.IsAny<int>(), It.IsAny<PluginRuntimeAgent>()), Times.Never);
        var record = (await runtimeStore.GetFreshByTaskVersionIdsAsync(
            [taskVersion.Id],
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CancellationToken.None)).Single();
        record.CurrentStatus.ShouldBe(PluginTaskVersionRuntimeStatus.Quarantined);
    }

    [Test]
    public async Task ReloadAsync_runtime_status_store_failure_does_not_crash_reload()
    {
        var runtimeStore = new Mock<IPluginTaskVersionRuntimeStatusStore>();
        runtimeStore
            .Setup(store => store.UpsertAsync(It.IsAny<PluginTaskVersionRuntimeStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        var taskVersion = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var agentManager = new Mock<IAgentManager>();
        agentManager
            .Setup(manager => manager.AgentExists(taskVersion.Id))
            .Returns(true);
        var orchestrator = CreateOrchestrator(
            [taskVersion],
            agentManager: agentManager.Object,
            runtimeStatusStore: runtimeStore.Object);

        await Should.NotThrowAsync(() => orchestrator.ReloadAsync(CancellationToken.None));
    }

    private static PluginRuntimeOrchestrator CreateOrchestrator(
        IReadOnlyList<TaskDefinitionInfo> taskVersions,
        IAgentManager? agentManager = null,
        InMemoryPluginTaskVersionRuntimeStatusStore? runtimeStore = null,
        IPluginTaskVersionRuntimeStatusStore? runtimeStatusStore = null,
        IPluginRuntimeCapabilityService? capabilityService = null,
        IPluginWorkspaceFactoryUtility? workspaceFactory = null,
        PluginAgentHealthPolicy? healthPolicy = null)
    {
        var repository = new Mock<ITaskDefinitionRepository>();
        repository
            .Setup(candidate => candidate.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. taskVersions]);

        var services = new ServiceCollection()
            .AddSingleton(repository.Object)
            .AddSingleton(workspaceFactory ?? Mock.Of<IPluginWorkspaceFactoryUtility>())
            .AddSingleton<IPluginBundleSource>(Mock.Of<IPluginBundleSource>())
            .BuildServiceProvider();

        var store = runtimeStatusStore ?? runtimeStore ?? new InMemoryPluginTaskVersionRuntimeStatusStore();
        return new PluginRuntimeOrchestrator(
            agentManager ?? Mock.Of<IAgentManager>(),
            capabilityService ?? Mock.Of<IPluginRuntimeCapabilityService>(),
            new PluginAbiCompatibilityService(),
            new InMemoryPluginIngestionStatusStore(),
            new PluginTaskVersionRuntimeStatusPublisher(
                store,
                new EngineInstanceInfo(),
            NullLogger<PluginTaskVersionRuntimeStatusPublisher>.Instance),
            new PluginRuntimeCatalog(),
            new PluginAgentHealthEventBus(),
            healthPolicy ?? new PluginAgentHealthPolicy(new PluginAgentHealthPolicyOptions(), new PluginAgentHealthTelemetry()),
            new TestAsyncDelay(),
            NullLogger<PluginRuntimeOrchestrator>.Instance,
            services.GetRequiredService<IServiceScopeFactory>());
    }

    private static IPluginRuntimeCapabilityService CapabilityServiceReturning(
        CapabilityValidationResult result)
    {
        var service = new Mock<IPluginRuntimeCapabilityService>();
        service
            .Setup(candidate => candidate.ValidateWorkspaceAsync(
                It.IsAny<FileInfo>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapabilityValidationReport { Results = [result] });
        return service.Object;
    }

    private static IPluginWorkspaceFactoryUtility WorkspaceFactoryReturningTempDirectory(
        TaskDefinitionInfo taskVersion)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflowbuilder-runtime-status-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var workspaceFactory = new Mock<IPluginWorkspaceFactoryUtility>();
        workspaceFactory
            .Setup(factory => factory.CreateWorkspaceAsync(taskVersion.Name, taskVersion.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(directory);
        return workspaceFactory.Object;
    }

    private static TaskDefinitionInfo CreateTaskVersion(
        int id,
        Version version,
        PluginAbiMetadata abi) =>
        new(
            id,
            10,
            "RuntimeAvailabilityPlugin",
            "app/RuntimeAvailabilityPlugin.dll",
            version,
            TaskVersionState.Registered,
            abi);
}
