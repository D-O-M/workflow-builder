using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
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

public sealed class PluginRuntimeOrchestratorIngestionStatusTests
{
    [Test]
    public async Task ReloadAsync_records_abi_invalid_for_incompatible_task_version()
    {
        var store = new InMemoryPluginIngestionStatusStore();
        var taskVersion = CreateTaskVersion(
            101,
            new Version(1, 0, 1),
            PluginAbiDefaults.CurrentMetadata with
            {
                ProcessProtocolVersion = 0
            });
        var orchestrator = CreateOrchestrator(
            [taskVersion],
            store: store);

        await orchestrator.ReloadAsync(CancellationToken.None);

        var record = await store.GetAsync(
            PluginIngestionStatusIds.FromPlugin(taskVersion.Name, taskVersion.Version),
            CancellationToken.None);
        record.ShouldNotBeNull();
        record.CurrentStatus.ShouldBe(PluginIngestionStatus.AbiInvalid);
        record.TaskVersionId.ShouldBe(taskVersion.Id);
    }

    [Test]
    public async Task ReloadAsync_records_runtime_load_failed_for_agent_startup_failure()
    {
        var store = new InMemoryPluginIngestionStatusStore();
        var taskVersion = CreateTaskVersion(
            101,
            new Version(1, 0, 1),
            PluginAbiDefaults.CurrentMetadata);
        var workspaceFactory = new Mock<IPluginWorkspaceFactoryUtility>();
        workspaceFactory
            .Setup(factory => factory.CreateWorkspaceAsync(taskVersion.Name, taskVersion.Version, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException(@"Could not create C:\temp\workflow\plugin AccountKey=abc123;"));

        var orchestrator = CreateOrchestrator(
            [taskVersion],
            store: store,
            workspaceFactory: workspaceFactory.Object);

        await orchestrator.ReloadAsync(CancellationToken.None);

        var record = await store.GetAsync(
            PluginIngestionStatusIds.FromPlugin(taskVersion.Name, taskVersion.Version),
            CancellationToken.None);
        record.ShouldNotBeNull();
        record.CurrentStatus.ShouldBe(PluginIngestionStatus.RuntimeLoadFailed);
        record.LastFailureMessage.ShouldNotBeNull();
        record.LastFailureMessage.ShouldNotContain(@"C:\temp\workflow");
        record.LastFailureMessage.ShouldNotContain("abc123");
    }

    [Test]
    public async Task ReloadAsync_status_recording_failure_does_not_crash_reload()
    {
        var store = new Mock<IPluginIngestionStatusStore>();
        store
            .Setup(candidate => candidate.AppendEventAsync(It.IsAny<PluginIngestionStatusUpdate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store unavailable"));
        var taskVersion = CreateTaskVersion(
            101,
            new Version(1, 0, 1),
            PluginAbiDefaults.CurrentMetadata with
            {
                ProcessProtocolVersion = 0
            });
        var orchestrator = CreateOrchestrator(
            [taskVersion],
            store: store.Object);

        await Should.NotThrowAsync(() => orchestrator.ReloadAsync(CancellationToken.None));
    }

    private static PluginRuntimeOrchestrator CreateOrchestrator(
        IReadOnlyList<TaskDefinitionInfo> taskVersions,
        IPluginIngestionStatusStore? store = null,
        IPluginWorkspaceFactoryUtility? workspaceFactory = null)
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

        return new PluginRuntimeOrchestrator(
            Mock.Of<IAgentManager>(),
            Mock.Of<IPluginRuntimeCapabilityService>(),
            new PluginAbiCompatibilityService(),
            store ?? new InMemoryPluginIngestionStatusStore(),
            new PluginTaskVersionRuntimeStatusPublisher(
                new InMemoryPluginTaskVersionRuntimeStatusStore(),
                new EngineInstanceInfo(),
                NullLogger<PluginTaskVersionRuntimeStatusPublisher>.Instance),
            new PluginRuntimeCatalog(),
            new PluginAgentHealthEventBus(),
            new PluginAgentHealthPolicy(new PluginAgentHealthPolicyOptions(), new PluginAgentHealthTelemetry()),
            new TestAsyncDelay(),
            NullLogger<PluginRuntimeOrchestrator>.Instance,
            services.GetRequiredService<IServiceScopeFactory>());
    }

    private static TaskDefinitionInfo CreateTaskVersion(
        int id,
        Version version,
        PluginAbiMetadata abi) =>
        new(
            id,
            10,
            "RuntimeStatusPlugin",
            "app/RuntimeStatusPlugin.dll",
            version,
            TaskVersionState.Registered,
            abi);
}
