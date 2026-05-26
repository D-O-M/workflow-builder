using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.State.Objects;
using Backend.Dispatcher;
using Backend.Engine;
using Backend.Interfaces.Repositories;
using Backend.Maintenance;
using Backend.Models;
using Backend.ProjectionWorker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class RuntimeServiceBoundaryTests
{
    [Test]
    public void Engine_program_hosts_execution_kernel_but_not_dispatch_or_maintenance_loops()
    {
        var source = File.ReadAllText(RepositoryPath("Backend.Engine", "Program.cs"));

        source.ShouldContain("AddHostedService<QueueConsumer>");
        source.ShouldContain("AddHostedService<PluginResultFinalizationWorker>");
        source.ShouldContain("AddHostedService<PluginReloadConsumer>");
        source.ShouldNotContain("AddHostedService<ProcessStepDispatchWorker>");
        source.ShouldNotContain("AddHostedService<StepFinalizationRecoveryWorker>");
        source.ShouldNotContain("AddHostedService<ProcessStartReconciliationWorker>");
        source.ShouldNotContain("PluginBlobReconciliationHostedService");
        source.ShouldNotContain("PluginBlobDetectorFunction");
    }

    [Test]
    public void Engine_and_nft_wiring_support_async_projection_mode()
    {
        var appHostSource = File.ReadAllText(RepositoryPath("Backend.AppHost", "Program.cs"));
        var launchSettings = File.ReadAllText(RepositoryPath("Backend.AppHost", "Properties", "launchSettings.json"));
        var nftScript = File.ReadAllText(RepositoryPath("PerformanceTests", "scripts", "run-local-nft.ps1"));

        appHostSource.ShouldContain("\"Projections__SynchronousEngineUpdatesEnabled\"");
        appHostSource.ShouldContain("!nftMinimalServices || nftEnabled");
        launchSettings.ShouldContain("\"ProjectionWorker__Enabled\": \"true\"");
        launchSettings.ShouldContain("\"Projections__SynchronousEngineUpdatesEnabled\": \"false\"");
        nftScript.ShouldContain("\"ProjectionWorker__Enabled\" = \"true\"");
        nftScript.ShouldContain("\"Projections__SynchronousEngineUpdatesEnabled\" = \"false\"");
        nftScript.ShouldNotContain("\"backend-projection-worker\"");
    }

    [Test]
    public void Engine_projection_call_sites_are_guarded_by_projection_policy()
    {
        File.ReadAllText(RepositoryPath("Backend.Engine", "StepClaimService.cs"))
            .ShouldContain("_projectionOptions.SynchronousEngineUpdatesEnabled");
        File.ReadAllText(RepositoryPath("Backend.Engine", "StepFinalizationService.cs"))
            .ShouldContain("_projectionOptions.SynchronousEngineUpdatesEnabled");
        File.ReadAllText(RepositoryPath("Backend.Engine", "ActivationPersistenceService.cs"))
            .ShouldContain("_projectionOptions.SynchronousEngineUpdatesEnabled");
    }

    [Test]
    public void Engine_registers_active_process_step_lock_renewal()
    {
        var programSource = File.ReadAllText(RepositoryPath("Backend.Engine", "Program.cs"));
        var executionSource = File.ReadAllText(RepositoryPath("Backend.Engine", "StepExecutionService.cs"));
        var renewalSource = File.ReadAllText(RepositoryPath("Backend.Engine", "ActiveStepLockRenewalService.cs"));

        programSource.ShouldContain("AddSingleton<ActiveStepLockRenewalService>");
        programSource.ShouldContain("AddSingleton<ProcessStepLockRenewalTelemetry>");
        programSource.ShouldContain("AddMeter(ProcessStepLockRenewalTelemetry.MeterName)");
        executionSource.ShouldContain("_lockRenewalService.Start(context)");
        executionSource.ShouldContain("StopLockRenewalAsync");
        renewalSource.ShouldContain("RenewProcessingWindowAsync");
        renewalSource.ShouldNotContain("MaxAutoLockRenewalDuration");
    }

    [Test]
    public void Dispatcher_registers_dispatch_worker_only()
    {
        var services = new ServiceCollection();
        services.AddDispatcherWorker(new ConfigurationBuilder().Build());

        HostedServiceTypes(services).ShouldContain(typeof(ProcessStepDispatchWorker));
        AllImplementationTypes(services).ShouldNotContain(typeof(QueueConsumer));
        AllImplementationTypes(services).ShouldNotContain(typeof(PluginRuntimeOrchestrator));
        AllImplementationTypes(services).ShouldNotContain(typeof(PluginRuntimeAgent));
        AllImplementationTypes(services).ShouldNotContain(typeof(PluginReloadConsumer));
    }

    [Test]
    public void Maintenance_registers_recovery_and_reconciliation_without_engine_runtime_hosts()
    {
        var services = new ServiceCollection();
        services.AddMaintenanceWorkers(new ConfigurationBuilder().Build());

        HostedServiceTypes(services).ShouldContain(typeof(StepFinalizationRecoveryWorker));
        HostedServiceTypes(services).ShouldContain(typeof(ProcessStartReconciliationWorker));
        AllImplementationTypes(services).ShouldNotContain(typeof(QueueConsumer));
        AllImplementationTypes(services).ShouldNotContain(typeof(PluginRuntimeOrchestrator));
        AllImplementationTypes(services).ShouldNotContain(typeof(PluginRuntimeAgent));
        AllImplementationTypes(services).ShouldNotContain(typeof(PluginReloadConsumer));
    }

    [Test]
    public async Task Projection_worker_projects_process_changes_to_index_and_realtime_summaries()
    {
        var blueprint = CreateBlueprint();
        var process = CreateProcess(Guid.NewGuid(), blueprint);
        var index = new RecordingProcessIndexProjectionService();
        var realtime = new RecordingProcessRealtimeProjectionService();
        var handler = new ProcessProjectionChangeHandler(
            new BlueprintExecutionCacheStub(blueprint),
            index,
            realtime,
            NullLogger<ProcessProjectionChangeHandler>.Instance);

        await handler.ProjectProcessChangesAsync([process], CancellationToken.None);

        index.ProcessUpserts[(process.ProcessId, blueprint.Id)].ShouldBe(1);
        realtime.ProcessUpserts[(process.ProcessId, blueprint.Id)].ShouldBe(1);
    }

    [Test]
    public async Task Projection_worker_projects_step_changes_to_realtime_nodes()
    {
        var blueprint = CreateBlueprint();
        var step = CreateStep(Guid.NewGuid(), Guid.NewGuid(), blueprint);
        var realtime = new RecordingProcessRealtimeProjectionService();
        var handler = new ProcessProjectionChangeHandler(
            new BlueprintExecutionCacheStub(blueprint),
            new RecordingProcessIndexProjectionService(),
            realtime,
            NullLogger<ProcessProjectionChangeHandler>.Instance);

        await handler.ProjectStepChangesAsync([step, step], CancellationToken.None);

        realtime.StepUpserts[(step.Id, blueprint.Id)].ShouldBe(2);
        realtime.UpsertedStepIds.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
    }

    [Test]
    public async Task Projection_repair_rebuilds_index_and_realtime_from_canonical_records()
    {
        var blueprint = CreateBlueprint();
        var processId = Guid.NewGuid();
        var process = CreateProcess(processId, blueprint);
        var step = CreateStep(processId, blueprint.InitialNode.Id, blueprint);
        var index = new RecordingProcessIndexProjectionService();
        var realtime = new RecordingProcessRealtimeProjectionService();
        var repair = new ProcessProjectionRepairService(
            new BlueprintExecutionCacheStub(blueprint),
            new ProcessInstanceRepositoryStub(process),
            new ProcessStepRepositoryStub([step]),
            index,
            realtime);

        await repair.RepairProcessAsync(processId, blueprint.Id, CancellationToken.None);

        index.ProcessUpserts[(process.ProcessId, blueprint.Id)].ShouldBe(1);
        realtime.RepairUpserts[(process.ProcessId, blueprint.Id)].ShouldBe(1);
        realtime.RepairedStepIds.ShouldBe([step.Id]);
    }

    [Test]
    public void Projection_worker_telemetry_records_processed_items_failures_and_lag()
    {
        var longMeasurements = new List<(string Instrument, long Value)>();
        var doubleMeasurements = new List<(string Instrument, double Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProjectionWorkerTelemetry.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            longMeasurements.Add((instrument.Name, measurement)));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            doubleMeasurements.Add((instrument.Name, measurement)));
        listener.Start();

        var telemetry = new ProjectionWorkerTelemetry();
        var observedAt = DateTimeOffset.UtcNow;
        telemetry.RecordProcessed("processes", 2);
        telemetry.RecordFailure("processsteps", "test_failure");
        telemetry.RecordLag("processes", observedAt.AddMilliseconds(-25), observedAt);

        longMeasurements.ShouldContain(measurement =>
            measurement.Instrument == "projection_worker_processed_items_total" &&
            measurement.Value == 2);
        longMeasurements.ShouldContain(measurement =>
            measurement.Instrument == "projection_worker_failures_total" &&
            measurement.Value == 1);
        doubleMeasurements.ShouldContain(measurement =>
            measurement.Instrument == "projection_worker_lag_ms" &&
            measurement.Value >= 0);
    }

    private static IEnumerable<Type> HostedServiceTypes(IServiceCollection services) =>
        services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .OfType<Type>();

    private static IEnumerable<Type> AllImplementationTypes(IServiceCollection services) =>
        services
            .SelectMany(descriptor => new[] { descriptor.ServiceType, descriptor.ImplementationType })
            .OfType<Type>();

    private static string RepositoryPath(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(parts)} from {AppContext.BaseDirectory}.");
    }

    private static ExecutableBlueprint CreateBlueprint()
    {
        var node = new ExecutableBlueprintNode
        {
            Id = Guid.NewGuid(),
            Name = "Initial",
            TaskDefinitionId = 42,
            AllowedMajor = 1,
            AllowedMinor = 0,
            ResolvedTaskVersionId = 4201,
            ResolvedVersion = new Version(1, 0, 0),
            ExecutionOrder = 0,
            InputChannel = new ExecutableBlueprintChannel
            {
                Id = 1,
                Name = "Input",
                Properties = [],
                OutboundBindings = []
            },
            OutputChannels = new Dictionary<string, ExecutableBlueprintChannel>(StringComparer.Ordinal),
            InboundBindings = [],
            DesignTimeInputOverrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };

        return new ExecutableBlueprint
        {
            Id = 101,
            DefinitionId = 7,
            VersionNumber = 1,
            Name = "Projection Blueprint",
            InitialNode = node,
            AllNodes = [node]
        };
    }

    private static ProcessInstanceRecord CreateProcess(
        Guid processId,
        ExecutableBlueprint blueprint) =>
        new()
        {
            id = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = ProcessStatus.InProgress,
            NodeOverrides = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal),
            ExecutionProgress = new ProcessExecutionProgress
            {
                TotalNodeCount = 1,
                TotalStepCount = 1
            }
        };

    private static ProcessStepRecord CreateStep(
        Guid processId,
        Guid blueprintNodeId,
        ExecutableBlueprint blueprint) =>
        new()
        {
            Id = ProcessStepIdentity.Create(processId, blueprintNodeId).ToString(),
            ProcessId = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            BlueprintNodeId = blueprintNodeId.ToString(),
            TaskDefinitionId = 42,
            AllowedMajor = 1,
            AllowedMinor = 0,
            Status = ProcessStepStatus.Completed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            OutputData = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        };

    private sealed class RecordingProcessIndexProjectionService : IProcessIndexProjectionService
    {
        public Dictionary<(string ProcessId, int BlueprintVersionId), int> ProcessUpserts { get; } = [];

        public Task UpsertFromProcessAsync(
            ProcessInstanceRecord process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            var key = (process.ProcessId, blueprintVersionId);
            ProcessUpserts[key] = ProcessUpserts.GetValueOrDefault(key) + 1;
            return Task.CompletedTask;
        }

        public Task<ProcessDirectoryRecord?> GetDirectoryByProcessIdAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessSummaryProjectionPage> GetRecentTenantSummariesAsync(
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessSummaryProjectionPage> GetRecentBlueprintSummariesAsync(
            int blueprintDefinitionId,
            IReadOnlyCollection<int>? blueprintVersionIds,
            string? continuationToken,
            int maxItemCount,
            DateTimeOffset? createdFrom,
            DateTimeOffset? createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingProcessRealtimeProjectionService : IProcessRealtimeProjectionService
    {
        public Dictionary<(string ProcessId, int BlueprintVersionId), int> ProcessUpserts { get; } = [];

        public Dictionary<(string ProcessStepId, int BlueprintVersionId), int> StepUpserts { get; } = [];

        public Dictionary<(string ProcessId, int BlueprintVersionId), int> RepairUpserts { get; } = [];

        public List<string> UpsertedStepIds { get; } = [];

        public List<string> RepairedStepIds { get; private set; } = [];

        public Task UpsertFromProcessAsync(
            ProcessInstanceRecord process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            var key = (process.ProcessId, blueprintVersionId);
            ProcessUpserts[key] = ProcessUpserts.GetValueOrDefault(key) + 1;
            return Task.CompletedTask;
        }

        public Task UpsertFromStepAsync(
            ProcessStepRecord step,
            int blueprintDefinitionId,
            int blueprintVersionId,
            CancellationToken cancellationToken)
        {
            var key = (step.Id, blueprintVersionId);
            StepUpserts[key] = StepUpserts.GetValueOrDefault(key) + 1;
            UpsertedStepIds.Add(step.Id);
            return Task.CompletedTask;
        }

        public Task UpsertFromProcessAndStepsAsync(
            ProcessInstanceRecord process,
            int blueprintDefinitionId,
            int blueprintVersionId,
            IReadOnlyCollection<ProcessStepRecord> steps,
            CancellationToken cancellationToken)
        {
            var key = (process.ProcessId, blueprintVersionId);
            RepairUpserts[key] = RepairUpserts.GetValueOrDefault(key) + 1;
            RepairedStepIds = steps.Select(step => step.Id).ToList();
            return Task.CompletedTask;
        }

        public Task<ProcessRealtimeSummaryProjectionRecord?> GetSummaryAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>> GetNodeProjectionsAsync(
            Guid processId,
            IReadOnlyCollection<Guid> blueprintNodeIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BlueprintExecutionCacheStub : IBlueprintExecutionCache
    {
        private readonly ExecutableBlueprint _blueprint;

        public BlueprintExecutionCacheStub(ExecutableBlueprint blueprint)
        {
            _blueprint = blueprint;
        }

        public Task<ExecutableBlueprint> GetExecutableBlueprintAsync(
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            blueprintVersionId == _blueprint.Id
                ? Task.FromResult(_blueprint)
                : throw new KeyNotFoundException();
    }

    private sealed class ProcessInstanceRepositoryStub
        : IProcessInstanceRepository
    {
        private readonly ProcessInstanceRecord _process;

        public ProcessInstanceRepositoryStub(ProcessInstanceRecord process)
        {
            _process = process;
        }

        public Task<ProcessInstanceRecord> GetProcessStateById(
            Guid processId,
            int blueprintVersionId,
            CancellationToken cancellationToken) =>
            processId.ToString() == _process.ProcessId && blueprintVersionId.ToString() == _process.BlueprintId
                ? Task.FromResult(_process)
                : throw new KeyNotFoundException();

        public Task<ProcessInstanceRecord> CreateProcessState(
            ProcessInstanceRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<List<ProcessInstanceRecord>> GetProcessStatesForBlueprint(
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

        public Task<ProcessInstanceRecord> UpdateProcessState(
            ProcessInstanceRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessState(
            ProcessInstanceRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ProcessStepRepositoryStub : IProcessStepRepository
    {
        private readonly IReadOnlyList<ProcessStepRecord> _steps;

        public ProcessStepRepositoryStub(IReadOnlyList<ProcessStepRecord> steps)
        {
            _steps = steps;
        }

        public Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_steps
                .Where(step => step.ProcessId == processId.ToString())
                .ToList());

        public Task<ProcessStepRecord> CreateProcessStep(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> GetProcessStepById(
            Guid taskInstanceId,
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(bool Claimed, ProcessStepRecord? State)> TryClaimProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            int taskDefinitionId,
            int allowedMajor,
            int allowedMinor,
            int resolvedTaskVersionId,
            string resolvedTaskVersion,
            DateTimeOffset startedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            Dictionary<string, JsonElement> outputData,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailProcessStepAsync(
            Guid taskInstanceId,
            Guid processId,
            Dictionary<string, JsonElement>? effectiveInputData,
            string? errorMessage,
            DateTimeOffset failedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProcessStepRecord> UpdateProcessStep(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryUpdateProcessStep(
            ProcessStepRecord state,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
