using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.Relational.Entities;
using Backend.Engine;
using Backend.Engine.Capabilities;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.PluginAbi;
using Backend.Services.PluginIngestion;
using Backend.Services.PluginRuntimeStatus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginRuntimeOrchestratorAbiTests
{
    [Test]
    public async Task ReloadAsync_routes_compatible_task_version()
    {
        var catalog = new PluginRuntimeCatalog();
        var compatible = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var agentManager = new Mock<IAgentManager>();
        agentManager
            .Setup(manager => manager.AgentExists(compatible.Id))
            .Returns(true);

        var orchestrator = CreateOrchestrator(catalog, agentManager.Object, [compatible]);

        await orchestrator.ReloadAsync(CancellationToken.None);

        catalog.TryResolvePluginRuntime(
            compatible.TaskDefinitionId,
            compatible.Version.Major,
            compatible.Version.Minor,
            out var resolution).ShouldBeTrue();
        resolution.TaskVersionId.ShouldBe(compatible.Id);
    }

    [Test]
    public async Task ReloadAsync_skips_incompatible_task_version_without_starting_agent()
    {
        var catalog = new PluginRuntimeCatalog();
        var incompatible = CreateTaskVersion(
            101,
            new Version(1, 0, 1),
            PluginAbiDefaults.CurrentMetadata with
            {
                Version = new PluginAbiVersion(2, 0)
            });
        var agentManager = new Mock<IAgentManager>();

        var orchestrator = CreateOrchestrator(catalog, agentManager.Object, [incompatible]);

        await orchestrator.ReloadAsync(CancellationToken.None);

        catalog.TryResolvePluginRuntime(10, 1, 0, out _).ShouldBeFalse();
        agentManager.Verify(manager => manager.RegisterAgent(It.IsAny<int>(), It.IsAny<PluginRuntimeAgent>()), Times.Never);
    }

    [Test]
    public async Task ReloadAsync_skips_legacy_process_protocol_task_version()
    {
        var catalog = new PluginRuntimeCatalog();
        var legacyProtocol = CreateTaskVersion(
            101,
            new Version(1, 0, 1),
            PluginAbiDefaults.CurrentMetadata with
            {
                ProcessProtocolVersion = 0
            });
        var agentManager = new Mock<IAgentManager>();

        var orchestrator = CreateOrchestrator(catalog, agentManager.Object, [legacyProtocol]);

        await orchestrator.ReloadAsync(CancellationToken.None);

        catalog.TryResolvePluginRuntime(10, 1, 0, out _).ShouldBeFalse();
        agentManager.Verify(manager => manager.RegisterAgent(It.IsAny<int>(), It.IsAny<PluginRuntimeAgent>()), Times.Never);
    }

    [Test]
    public async Task ReloadAsync_falls_back_to_compatible_candidate_when_newest_is_incompatible()
    {
        var catalog = new PluginRuntimeCatalog();
        var incompatible = CreateTaskVersion(
            102,
            new Version(1, 0, 2),
            PluginAbiDefaults.CurrentMetadata with
            {
                Version = new PluginAbiVersion(2, 0)
            });
        var compatible = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var agentManager = new Mock<IAgentManager>();
        agentManager
            .Setup(manager => manager.AgentExists(compatible.Id))
            .Returns(true);

        var orchestrator = CreateOrchestrator(catalog, agentManager.Object, [incompatible, compatible]);

        await orchestrator.ReloadAsync(CancellationToken.None);

        catalog.TryResolvePluginRuntime(10, 1, 0, out var resolution).ShouldBeTrue();
        resolution.TaskVersionId.ShouldBe(compatible.Id);
    }

    private static PluginRuntimeOrchestrator CreateOrchestrator(
        PluginRuntimeCatalog catalog,
        IAgentManager agentManager,
        IReadOnlyList<TaskDefinitionInfo> taskVersions)
    {
        var repository = new Mock<ITaskDefinitionRepository>();
        repository
            .Setup(candidate => candidate.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. taskVersions]);

        var services = new ServiceCollection()
            .AddSingleton(repository.Object)
            .BuildServiceProvider();

        return new PluginRuntimeOrchestrator(
            agentManager,
            Mock.Of<IPluginRuntimeCapabilityService>(),
            new PluginAbiCompatibilityService(),
            new InMemoryPluginIngestionStatusStore(),
            new PluginTaskVersionRuntimeStatusPublisher(
                new InMemoryPluginTaskVersionRuntimeStatusStore(),
                new EngineInstanceInfo(),
                NullLogger<PluginTaskVersionRuntimeStatusPublisher>.Instance),
            catalog,
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
            "AbiPlugin",
            "app/AbiPlugin.dll",
            version,
            TaskVersionState.Registered,
            abi);
}
