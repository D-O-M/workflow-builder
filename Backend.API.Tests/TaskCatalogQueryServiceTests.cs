using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Models.DomainModels;
using Backend.Models.ViewModels;
using Backend.Services.PluginRuntimeStatus;
using Shouldly;

namespace Backend.API.Tests;

public sealed class TaskCatalogQueryServiceTests
{
    [Test]
    public async Task GetTaskCatalogAsync_returns_unknown_not_loaded_when_no_fresh_runtime_record_exists()
    {
        var service = CreateService(CreateTaskDefinition());

        var tasks = await service.GetTaskCatalogAsync(CancellationToken.None);

        var availability = tasks.Single().Versions.Single().RuntimeAvailability;
        availability.Status.ShouldBe(PluginTaskVersionRuntimeStatus.UnknownNotLoaded.ToString());
        availability.Runnable.ShouldBeFalse();
        availability.ReportingEngineCount.ShouldBe(0);
    }

    [Test]
    public async Task GetTaskCatalogAsync_exposes_fresh_runnable_record()
    {
        var store = new InMemoryPluginTaskVersionRuntimeStatusStore();
        await PublishAsync(store, 101, "engine-a", PluginTaskVersionRuntimeStatus.Runnable);
        var service = CreateService(CreateTaskDefinition(), store);

        var tasks = await service.GetTaskCatalogAsync(CancellationToken.None);

        var availability = tasks.Single().Versions.Single().RuntimeAvailability;
        availability.Status.ShouldBe(PluginTaskVersionRuntimeStatus.Runnable.ToString());
        availability.Runnable.ShouldBeTrue();
        availability.ReportingEngineCount.ShouldBe(1);
    }

    [Test]
    public async Task GetTaskCatalogAsync_exposes_runtime_load_failure()
    {
        var store = new InMemoryPluginTaskVersionRuntimeStatusStore();
        await PublishAsync(
            store,
            101,
            "engine-a",
            PluginTaskVersionRuntimeStatus.RuntimeLoadFailed,
            @"Could not create C:\temp\workflow\plugin AccountKey=abc123;");
        var service = CreateService(CreateTaskDefinition(), store);

        var tasks = await service.GetTaskCatalogAsync(CancellationToken.None);

        var availability = tasks.Single().Versions.Single().RuntimeAvailability;
        availability.Status.ShouldBe(PluginTaskVersionRuntimeStatus.RuntimeLoadFailed.ToString());
        availability.Runnable.ShouldBeFalse();
        availability.Message.ShouldNotBeNull();
        availability.Message.ShouldNotContain(@"C:\temp\workflow");
        availability.Message.ShouldNotContain("abc123");
    }

    [Test]
    public async Task GetTaskCatalogAsync_prefers_any_fresh_runnable_record_over_other_engine_failures()
    {
        var store = new InMemoryPluginTaskVersionRuntimeStatusStore();
        await PublishAsync(store, 101, "engine-a", PluginTaskVersionRuntimeStatus.RuntimeLoadFailed);
        await PublishAsync(store, 101, "engine-b", PluginTaskVersionRuntimeStatus.Runnable);
        var service = CreateService(CreateTaskDefinition(), store);

        var tasks = await service.GetTaskCatalogAsync(CancellationToken.None);

        var availability = tasks.Single().Versions.Single().RuntimeAvailability;
        availability.Status.ShouldBe(PluginTaskVersionRuntimeStatus.Runnable.ToString());
        availability.Runnable.ShouldBeTrue();
        availability.ReportingEngineCount.ShouldBe(2);
    }

    [Test]
    public async Task GetTaskCatalogAsync_ignores_stale_runtime_records()
    {
        var store = new InMemoryPluginTaskVersionRuntimeStatusStore();
        await PublishAsync(
            store,
            101,
            "engine-a",
            PluginTaskVersionRuntimeStatus.Runnable,
            occurredAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var service = CreateService(CreateTaskDefinition(), store, staleAfterSeconds: 60);

        var tasks = await service.GetTaskCatalogAsync(CancellationToken.None);

        var availability = tasks.Single().Versions.Single().RuntimeAvailability;
        availability.Status.ShouldBe(PluginTaskVersionRuntimeStatus.UnknownNotLoaded.ToString());
        availability.Runnable.ShouldBeFalse();
    }

    [Test]
    public async Task GetTaskCatalogAsync_compacts_and_sanitizes_runtime_issues()
    {
        var store = new InMemoryPluginTaskVersionRuntimeStatusStore();
        await PublishAsync(
            store,
            101,
            "engine-a",
            PluginTaskVersionRuntimeStatus.UnrunnableMissingCapability,
            issues:
            [
                .. Enumerable.Range(1, 6).Select(index => new PluginTaskVersionRuntimeIssueRecord
                {
                    CapabilityId = $"capability:{index}",
                    DisplayName = $"Capability {index}",
                    Kind = "software",
                    Status = "Missing",
                    Category = "MissingCapability",
                    Message = index == 1 ? @"C:\temp\workflow\tool.exe missing Token=abc123" : $"issue {index}",
                    Remediation = "install tool"
                })
            ]);
        var service = CreateService(CreateTaskDefinition(), store);

        var tasks = await service.GetTaskCatalogAsync(CancellationToken.None);

        var issues = tasks.Single().Versions.Single().RuntimeAvailability.Issues;
        issues.Count.ShouldBe(5);
        var message = issues[0].Message.ShouldNotBeNull();
        message.ShouldNotContain(@"C:\temp\workflow");
        message.ShouldNotContain("abc123");
    }

    private static TaskCatalogQueryService CreateService(
        TaskDefinitionViewModel taskDefinition,
        InMemoryPluginTaskVersionRuntimeStatusStore? store = null,
        int staleAfterSeconds = 120) =>
        new(
            new TaskDefinitionRepositoryStub([taskDefinition]),
            store ?? new InMemoryPluginTaskVersionRuntimeStatusStore(),
            new PluginRuntimeStatusOptions
            {
                StaleAfterSeconds = staleAfterSeconds
            });

    private static TaskDefinitionViewModel CreateTaskDefinition() =>
        new()
        {
            Id = 10,
            Name = "RuntimeAvailabilityPlugin",
            Versions =
            [
                new TaskVersionViewModel
                {
                    Id = 101,
                    Version = "1.0.0",
                    State = TaskVersionState.Registered.ToString(),
                    OutputChannels = []
                }
            ]
        };

    private static Task PublishAsync(
        InMemoryPluginTaskVersionRuntimeStatusStore store,
        int taskVersionId,
        string engineInstanceId,
        PluginTaskVersionRuntimeStatus status,
        string? message = null,
        IReadOnlyList<PluginTaskVersionRuntimeIssueRecord>? issues = null,
        DateTimeOffset? occurredAt = null) =>
        store.UpsertAsync(
            new PluginTaskVersionRuntimeStatusUpdate
            {
                TaskVersionId = taskVersionId,
                TaskDefinitionId = 10,
                PluginName = "RuntimeAvailabilityPlugin",
                PluginVersion = new Version(1, 0, 0),
                EngineInstanceId = engineInstanceId,
                Status = status,
                Message = message,
                Issues = issues ?? [],
                OccurredAt = occurredAt
            },
            CancellationToken.None);

    private sealed class TaskDefinitionRepositoryStub : ITaskDefinitionRepository
    {
        private readonly List<TaskDefinitionViewModel> _taskDefinitions;

        public TaskDefinitionRepositoryStub(List<TaskDefinitionViewModel> taskDefinitions)
        {
            _taskDefinitions = taskDefinitions;
        }

        public Task<List<TaskDefinitionInfo>> GetAll(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<TaskDefinitionViewModel>> GetAllTaskDefinitionViewModels(CancellationToken cancellationToken) =>
            Task.FromResult(_taskDefinitions);

        public Task CreateTaskSchema(Manifest manifest, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateBasicTask(string name, string version, string entryPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskVersionRegistrationResult> CreateVersion(TaskVersionDomainModel version, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
