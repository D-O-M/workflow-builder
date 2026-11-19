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
using Backend.Services.EngineManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginRuntimeOrchestratorAbiTests
{
    [Test]
    public async Task Replicas_apply_plugin_and_version_rules_independently_within_the_original_lane()
    {
        var newer = CreateTaskVersion(102, new Version(1, 0, 2), PluginAbiDefaults.CurrentMetadata);
        var older = CreateTaskVersion(101, new Version(1, 0, 1), PluginAbiDefaults.CurrentMetadata);
        var manager = new Mock<IAgentManager>();
        manager.Setup(m => m.AgentExists(It.IsAny<int>())).Returns(true);
        var policy = new EnginePolicySnapshot(1, new Dictionary<string, ManagedSetting>(),
            new HashSet<int>(), new HashSet<int> { newer.Id });
        var reader = new Mock<IEnginePolicyReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => policy);
        var reported = new Dictionary<string, long?>();
        var reporter = new Mock<IEngineReplicaReporter>();
        reporter.Setup(r => r.ReportAsync(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyCollection<int>?>()))
            .Callback<string, long?, string?, CancellationToken, IReadOnlyCollection<int>?>(
                (id, revision, _, _, _) => reported[id] = revision)
            .Returns(Task.CompletedTask);
        var firstCatalog = new PluginRuntimeCatalog();
        var secondCatalog = new PluginRuntimeCatalog();
        var first = CreateOrchestrator(firstCatalog, manager.Object, [newer, older], reader.Object, reporter.Object);
        var second = CreateOrchestrator(secondCatalog, manager.Object, [newer, older], reader.Object, reporter.Object);

        await first.ReloadAsync(CancellationToken.None);
        firstCatalog.TryResolvePluginRuntime(10, 1, 0, out var patch).ShouldBeTrue();
        patch.TaskVersionId.ShouldBe(older.Id);

        policy = policy with { Revision = 2, DisabledPlugins = new HashSet<int> { 10 } };
        await first.ReloadAsync(CancellationToken.None);
        await second.ReloadAsync(CancellationToken.None); // Newly started replica.
        secondCatalog.TryGetDisabledLane(10, 1, 0, out var blockedRevision).ShouldBeTrue();
        blockedRevision.ShouldBe(2);
        firstCatalog.TryResolvePluginRuntime(10, 1, 0, out _).ShouldBeFalse();
        reported.Count.ShouldBe(2);
        reported.Values.ShouldAllBe(revision => revision == 2);

        policy = policy with { Revision = 3, DisabledPlugins = new HashSet<int>() };
        await second.ReloadAsync(CancellationToken.None);
        secondCatalog.TryResolvePluginRuntime(10, 1, 0, out patch).ShouldBeTrue();
        patch.TaskVersionId.ShouldBe(older.Id);
        firstCatalog.TryGetDisabledLane(10, 1, 0, out _).ShouldBeTrue();
    }

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
        IReadOnlyList<TaskDefinitionInfo> taskVersions,
        IEnginePolicyReader? reader = null,
        IEngineReplicaReporter? reporter = null)
    {
        var repository = new Mock<ITaskDefinitionRepository>();
        repository
            .Setup(candidate => candidate.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. taskVersions]);

        var registrations = new ServiceCollection().AddSingleton(repository.Object);
        if (reader is not null)
        {
            registrations.AddSingleton(reader);
            registrations.AddSingleton(reporter!);
        }
        var services = registrations.BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EngineManagement:Enabled"] = (reader is not null).ToString()
        }).Build();

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
            new PluginAgentHealthEventBus(),
            new PluginAgentHealthPolicy(new PluginAgentHealthPolicyOptions(), new PluginAgentHealthTelemetry()),
            new TestAsyncDelay(),
            NullLogger<PluginRuntimeOrchestrator>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), config, new EngineInstanceInfo());
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
