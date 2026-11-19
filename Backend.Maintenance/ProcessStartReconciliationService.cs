using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Engine;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Repositories;
using Backend.Services.ProcessStepQueue;

namespace Backend.Maintenance;

public sealed record ProcessStartReconciliationResult(
    int CandidatesScanned,
    int MissingInitialStepsFound,
    int InitialStepsRepaired,
    int DispatchIntentsRepaired,
    int Failures);

public sealed class ProcessStartReconciliationService
{
    private static readonly ProcessStatus[] ActiveProcessStatuses =
    [
        ProcessStatus.Pending,
        ProcessStatus.InProgress
    ];

    private readonly IProcessInstanceRepository _processInstanceRepository;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessIndexProjectionService _processIndexProjectionService;
    private readonly IBlueprintExecutionCache _blueprintExecutionCache;
    private readonly IProcessStepDispatcher _processStepDispatcher;
    private readonly ProcessStartReconciliationTelemetry _telemetry;
    private readonly ILogger<ProcessStartReconciliationService> _logger;

    public ProcessStartReconciliationService(
        IProcessInstanceRepository processInstanceRepository,
        IProcessStepRepository processStepRepository,
        IProcessIndexProjectionService processIndexProjectionService,
        IBlueprintExecutionCache blueprintExecutionCache,
        IProcessStepDispatcher processStepDispatcher,
        ProcessStartReconciliationTelemetry telemetry,
        ILogger<ProcessStartReconciliationService> logger)
    {
        _processInstanceRepository = processInstanceRepository;
        _processStepRepository = processStepRepository;
        _processIndexProjectionService = processIndexProjectionService;
        _blueprintExecutionCache = blueprintExecutionCache;
        _processStepDispatcher = processStepDispatcher;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<ProcessStartReconciliationResult> ReconcileOnceAsync(
        ProcessStartReconciliationOptions options,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var createdFrom = now.Subtract(options.EffectiveMaxProcessAgeToScan);
        var candidates = await GetCandidatesAsync(
            createdFrom,
            options.EffectiveBatchSize,
            cancellationToken);

        var scanned = 0;
        var missingInitialStepsFound = 0;
        var initialStepsRepaired = 0;
        var dispatchIntentsRepaired = 0;
        var failures = 0;

        foreach (var candidate in candidates.Values
                     .OrderByDescending(candidate => candidate.CreatedAt)
                     .Take(options.EffectiveBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            try
            {
                var result = await ReconcileProcessAsync(candidate, cancellationToken);
                missingInitialStepsFound += result.MissingInitialStepFound ? 1 : 0;
                initialStepsRepaired += result.InitialStepRepaired ? 1 : 0;
                dispatchIntentsRepaired += result.DispatchIntentRepaired ? 1 : 0;
                failures += result.Failed ? 1 : 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                _telemetry.RecordFailure(exception);
                _logger.LogError(
                    exception,
                    "Process-start reconciliation failed unexpectedly for process {ProcessId}.",
                    candidate.ProcessId);
            }
        }

        return new ProcessStartReconciliationResult(
            scanned,
            missingInitialStepsFound,
            initialStepsRepaired,
            dispatchIntentsRepaired,
            failures);
    }

    private async Task<Dictionary<Guid, CandidateProcess>> GetCandidatesAsync(
        DateTimeOffset createdFrom,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<Guid, CandidateProcess>();

        try
        {
            var page = await _processIndexProjectionService.GetRecentTenantSummariesAsync(
                null,
                batchSize,
                createdFrom,
                null,
                ActiveProcessStatuses,
                cancellationToken);

            foreach (var summary in page.Items)
            {
                if (!Guid.TryParse(summary.ProcessId, out var processId))
                {
                    _telemetry.RecordFailure("invalid_summary_process_id");
                    _logger.LogWarning(
                        "Process-start reconciliation skipped summary with invalid process id {ProcessId}.",
                        summary.ProcessId);
                    continue;
                }

                candidates.TryAdd(
                    processId,
                    new CandidateProcess(
                        processId,
                        summary.BlueprintVersionId,
                        summary.CreatedAt,
                        Process: null));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _telemetry.RecordFailure(exception);
            _logger.LogWarning(
                exception,
                "Process-start reconciliation could not query process summaries; falling back to source process scan.");
        }

        try
        {
            var fallbackProcesses = await _processInstanceRepository.GetRecentProcessStatesForReconciliationAsync(
                createdFrom,
                batchSize,
                ActiveProcessStatuses,
                cancellationToken);

            foreach (var process in fallbackProcesses)
            {
                if (!Guid.TryParse(process.ProcessId, out var processId))
                {
                    _telemetry.RecordFailure("invalid_process_id");
                    _logger.LogWarning(
                        "Process-start reconciliation skipped process record with invalid process id {ProcessId}.",
                        process.ProcessId);
                    continue;
                }

                if (!int.TryParse(process.BlueprintId, out var blueprintVersionId))
                {
                    _telemetry.RecordFailure("invalid_blueprint_id");
                    _logger.LogWarning(
                        "Process-start reconciliation skipped process {ProcessId} with invalid blueprint id {BlueprintId}.",
                        process.ProcessId,
                        process.BlueprintId);
                    continue;
                }

                candidates[processId] = new CandidateProcess(
                    processId,
                    blueprintVersionId,
                    process.CreatedAt,
                    process);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _telemetry.RecordFailure(exception);
            _logger.LogWarning(
                exception,
                "Process-start reconciliation could not query fallback process records.");
        }

        return candidates;
    }

    private async Task<ProcessRepairResult> ReconcileProcessAsync(
        CandidateProcess candidate,
        CancellationToken cancellationToken)
    {
        var process = candidate.Process;
        if (process is null)
        {
            try
            {
                process = await _processInstanceRepository.GetProcessStateById(
                    candidate.ProcessId,
                    candidate.BlueprintVersionId,
                    cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                             IsMissingEntityException(exception))
            {
                return Fail(
                    "missing_process",
                    "Process-start reconciliation could not load process {ProcessId} for blueprint version {BlueprintVersionId}.",
                    candidate.ProcessId,
                    candidate.BlueprintVersionId);
            }
        }

        if (process.Status is ProcessStatus.Completed or ProcessStatus.Failed)
        {
            return ProcessRepairResult.Noop;
        }

        _processStepDispatcher.EnsureDispatchAvailableForExecution();

        ExecutableBlueprint blueprint;
        try
        {
            blueprint = await _blueprintExecutionCache.GetExecutableBlueprintAsync(
                candidate.BlueprintVersionId,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                         IsMissingEntityException(exception))
        {
            return Fail(
                "missing_blueprint",
                "Process-start reconciliation could not load blueprint version {BlueprintVersionId} for process {ProcessId}.",
                candidate.BlueprintVersionId,
                candidate.ProcessId);
        }

        var expectedInitialStepId = ProcessStepIdentity.Create(
            candidate.ProcessId,
            blueprint.InitialNode.Id);
        ProcessStepRecord? initialStep = null;
        var missingInitialStepFound = false;
        var initialStepRepaired = false;

        try
        {
            initialStep = await _processStepRepository.GetProcessStepById(
                expectedInitialStepId,
                candidate.ProcessId,
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                         IsMissingEntityException(exception))
        {
            missingInitialStepFound = true;
            _telemetry.RecordMissingInitialStep();

            if (process.Status is ProcessStatus.InProgress)
            {
                return Fail(
                    "missing_initial_step_in_progress",
                    "Process-start reconciliation found in-progress process {ProcessId} without initial step {ProcessStepId}; no step was recreated.",
                    candidate.ProcessId,
                    expectedInitialStepId,
                    missingInitialStepFound);
            }

            if (!TryCreateInitialStepFromSnapshot(
                    process,
                    blueprint,
                    candidate.ProcessId,
                    expectedInitialStepId,
                    out var repairedStep,
                    out var validationError))
            {
                return Fail(
                    "invalid_start_snapshot",
                    validationError,
                    candidate.ProcessId,
                    expectedInitialStepId,
                    missingInitialStepFound);
            }

            var createResult = await _processStepRepository.CreateProcessStepIfNotExists(
                repairedStep,
                cancellationToken);
            initialStep = createResult.State;
            initialStepRepaired = createResult.Created;
            if (createResult.Created)
            {
                _telemetry.RecordInitialStepRepaired();
                _logger.LogInformation(
                    "Process-start reconciliation repaired missing initial step {ProcessStepId} for process {ProcessId}.",
                    expectedInitialStepId,
                    candidate.ProcessId);
            }
        }

        var dispatchIntentRepaired = false;
        if (initialStep.IsDispatchable)
        {
            var previousActiveIntentId = initialStep.Dispatch?.ActiveIntentId;
            var hadActiveIntentProjection = HasActiveIntentProjection(initialStep);
            var dispatchIntent = await _processStepDispatcher.EnsureDispatchIntentAsync(
                initialStep,
                initialStep.CreatedAt,
                cancellationToken);
            dispatchIntentRepaired =
                !hadActiveIntentProjection ||
                !string.Equals(previousActiveIntentId, initialStep.Dispatch?.ActiveIntentId, StringComparison.Ordinal);
            if (dispatchIntentRepaired)
            {
                _telemetry.RecordDispatchIntentRepaired();
            }

            await _processStepDispatcher.TryDispatchAsync(
                initialStep,
                dispatchIntent,
                cancellationToken);
        }

        return new ProcessRepairResult(
            missingInitialStepFound,
            initialStepRepaired,
            dispatchIntentRepaired,
            Failed: false);
    }

    private bool TryCreateInitialStepFromSnapshot(
        ProcessInstanceRecord process,
        ExecutableBlueprint blueprint,
        Guid processId,
        Guid expectedInitialStepId,
        out ProcessStepRecord step,
        out string validationError)
    {
        step = null!;
        validationError = string.Empty;

        var start = process.Start;
        if (start is null)
        {
            validationError = $"Process {processId} is missing start snapshot and cannot recreate initial step {expectedInitialStepId}.";
            return false;
        }

        if (!Guid.TryParse(start.InitialBlueprintNodeId, out var snapshotNodeId) ||
            snapshotNodeId != blueprint.InitialNode.Id)
        {
            validationError = $"Process {processId} start snapshot initial node does not match blueprint version {blueprint.Id}.";
            return false;
        }

        if (!Guid.TryParse(start.InitialProcessStepId, out var snapshotStepId) ||
            snapshotStepId != expectedInitialStepId)
        {
            validationError = $"Process {processId} start snapshot initial step id does not match deterministic step id {expectedInitialStepId}.";
            return false;
        }

        if (start.InitialInputHydrationPlan is null)
        {
            validationError = $"Process {processId} start snapshot is missing the initial input hydration plan.";
            return false;
        }

        step = new ProcessStepRecord
        {
            Id = expectedInitialStepId.ToString(),
            BlueprintNodeId = blueprint.InitialNode.Id.ToString(),
            TaskDefinitionId = blueprint.InitialNode.TaskDefinitionId,
            AllowedMajor = blueprint.InitialNode.AllowedMajor,
            AllowedMinor = blueprint.InitialNode.AllowedMinor,
            ProcessId = processId.ToString(),
            BlueprintId = blueprint.Id.ToString(),
            CreatedAt = start.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Status = ProcessStepStatus.Pending,
            InputHydrationPlan = CloneInputHydrationPlan(start.InitialInputHydrationPlan),
            Dispatch = ProcessStepDispatchService.CreatePendingDispatchState(start.CreatedAt)
        };
        return true;
    }

    private ProcessRepairResult Fail(
        string reason,
        string message,
        object? arg1,
        object? arg2,
        bool missingInitialStepFound = false)
    {
        _telemetry.RecordFailure(reason);
        _logger.LogWarning(message, arg1, arg2);
        return new ProcessRepairResult(
            missingInitialStepFound,
            InitialStepRepaired: false,
            DispatchIntentRepaired: false,
            Failed: true);
    }

    private static bool HasActiveIntentProjection(ProcessStepRecord step) =>
        !string.IsNullOrWhiteSpace(step.Dispatch?.ActiveIntentId) &&
        step.Dispatch.DueBucket.HasValue &&
        step.Dispatch.Shard.HasValue;

    private static bool IsMissingEntityException(Exception exception) =>
        exception is EntityNotFoundException or KeyNotFoundException ||
        exception.GetType().Name.StartsWith("EntityNotFoundException", StringComparison.Ordinal);

    private static InputHydrationPlan CloneInputHydrationPlan(InputHydrationPlan source) =>
        new()
        {
            InputChannelName = source.InputChannelName,
            InitialInput = CloneJsonMap(source.InitialInput),
            DesignTimeInputOverrides = CloneJsonMap(source.DesignTimeInputOverrides),
            RuntimeInputOverrides = CloneJsonMap(source.RuntimeInputOverrides),
            Sources = source.Sources.Select(CloneInputHydrationSource).ToList()
        };

    private static InputHydrationSource CloneInputHydrationSource(InputHydrationSource source) =>
        new()
        {
            BindingId = source.BindingId,
            SourceProcessStepId = source.SourceProcessStepId,
            SourceOutputChannelName = source.SourceOutputChannelName,
            InlineInput = source.InlineInput is null ? null : CloneJsonMap(source.InlineInput),
            PropertyMappings = source.PropertyMappings
                .Select(mapping => new InputHydrationPropertyMapping
                {
                    SourceProperty = mapping.SourceProperty,
                    TargetProperty = mapping.TargetProperty
                })
                .ToList()
        };

    private static Dictionary<string, JsonElement> CloneJsonMap(
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null)
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        return values.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Clone(),
            StringComparer.Ordinal);
    }

    private sealed record CandidateProcess(
        Guid ProcessId,
        int BlueprintVersionId,
        DateTimeOffset CreatedAt,
        ProcessInstanceRecord? Process);

    private sealed record ProcessRepairResult(
        bool MissingInitialStepFound,
        bool InitialStepRepaired,
        bool DispatchIntentRepaired,
        bool Failed)
    {
        public static ProcessRepairResult Noop { get; } = new(
            MissingInitialStepFound: false,
            InitialStepRepaired: false,
            DispatchIntentRepaired: false,
            Failed: false);
    }
}
