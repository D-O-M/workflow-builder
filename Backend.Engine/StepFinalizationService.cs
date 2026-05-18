using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Repositories;

namespace Backend.Engine;

public sealed record CollectionIteratorFinalizationResult(
    int ItemCount,
    int QueuedStepCount,
    TimeSpan DownstreamActivationElapsed);

public interface IStepFinalizationService
{
    Task FinalizeSucceededStepAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken);

    Task FinalizeFailedStepAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken);

    Task<CollectionIteratorFinalizationResult> FinalizeSucceededCollectionIteratorStepAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        int repeatedBlueprintNodeCount,
        int itemCount,
        int batchSize,
        CancellationToken cancellationToken);

    Task FinalizeFailedCollectionIteratorStepAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken);

    Task<CollectionIteratorFinalizationResult> ResumeStepFinalizationAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken);

    Task<CollectionIteratorFinalizationResult> ResumeStepFinalizationForRecoveryAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken);
}

public class StepFinalizationService : IStepFinalizationService
{
    private const int MaxFinalizationUpdateAttempts = 10;
    private const int MaxProcessStateUpdateAttempts = 10;
    private const string CollectionIteratorOutputChannelName = "Item";
    private const string CollectionInputPropertyName = "items";

    private static readonly JsonSerializerOptions NormalizedJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ActivationPlanner _activationPlanner;
    private readonly ActivationPersistenceService _activationPersistenceService;
    private readonly PhasedProcessFinalizationService _phasedProcessFinalizationService;
    private readonly IProcessStepFinalizationRepository _finalizationRepository;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly IProcessIndexProjectionService? _processIndexProjectionService;
    private readonly StepClaimService _stepClaimService;
    private readonly ProcessLifecycleTelemetry _processLifecycleTelemetry;
    private readonly ILogger<StepFinalizationService> _logger;

    public StepFinalizationService(
        ActivationPlanner activationPlanner,
        ActivationPersistenceService activationPersistenceService,
        PhasedProcessFinalizationService phasedProcessFinalizationService,
        IProcessStepFinalizationRepository finalizationRepository,
        IProcessStepRepository processStepRepository,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        StepClaimService stepClaimService,
        ProcessLifecycleTelemetry processLifecycleTelemetry,
        ILogger<StepFinalizationService> logger,
        IProcessIndexProjectionService? processIndexProjectionService = null)
    {
        _activationPlanner = activationPlanner;
        _activationPersistenceService = activationPersistenceService;
        _phasedProcessFinalizationService = phasedProcessFinalizationService;
        _finalizationRepository = finalizationRepository;
        _processStepRepository = processStepRepository;
        _processInstanceRepository = processInstanceRepository;
        _processIndexProjectionService = processIndexProjectionService;
        _stepClaimService = stepClaimService;
        _processLifecycleTelemetry = processLifecycleTelemetry;
        _logger = logger;
    }

    public async Task FinalizeSucceededStepAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var record = await CreateOrGetFinalizationRecordAsync(
            context,
            executionResult,
            ProcessStepFinalizationSource.Plugin,
            collectionIteratorItemCount: null,
            collectionIteratorRepeatedBlueprintNodeCount: null,
            collectionIteratorBatchSize: null,
            cancellationToken);

        await RunWithRetryableErrorCheckpointAsync(
            record.Id,
            () => ContinueSuccessFinalizationAsync(
                context,
                record,
                completeQueueMessage: true,
                cancellationToken),
            cancellationToken);
    }

    public async Task FinalizeFailedStepAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var record = await CreateOrGetFinalizationRecordAsync(
            context,
            executionResult,
            ProcessStepFinalizationSource.Plugin,
            collectionIteratorItemCount: null,
            collectionIteratorRepeatedBlueprintNodeCount: null,
            collectionIteratorBatchSize: null,
            cancellationToken);

        await RunWithRetryableErrorCheckpointAsync(
            record.Id,
            () => ContinueFailureFinalizationAsync(
                context,
                record,
                completeQueueMessage: true,
                cancellationToken),
            cancellationToken);
    }

    public async Task<CollectionIteratorFinalizationResult> FinalizeSucceededCollectionIteratorStepAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        int repeatedBlueprintNodeCount,
        int itemCount,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var record = await CreateOrGetFinalizationRecordAsync(
            context,
            executionResult,
            ProcessStepFinalizationSource.CollectionIterator,
            itemCount,
            repeatedBlueprintNodeCount,
            batchSize,
            cancellationToken);

        return await RunWithRetryableErrorCheckpointAsync(
            record.Id,
            () => ContinueSuccessFinalizationAsync(
                context,
                record,
                completeQueueMessage: true,
                cancellationToken),
            cancellationToken);
    }

    public async Task FinalizeFailedCollectionIteratorStepAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var record = await CreateOrGetFinalizationRecordAsync(
            context,
            executionResult,
            ProcessStepFinalizationSource.CollectionIterator,
            collectionIteratorItemCount: null,
            collectionIteratorRepeatedBlueprintNodeCount: null,
            collectionIteratorBatchSize: null,
            cancellationToken);

        await RunWithRetryableErrorCheckpointAsync(
            record.Id,
            () => ContinueFailureFinalizationAsync(
                context,
                record,
                completeQueueMessage: true,
                cancellationToken),
            cancellationToken);
    }

    public async Task<CollectionIteratorFinalizationResult> ResumeStepFinalizationAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        return await ResumeStepFinalizationCoreAsync(
            context,
            record,
            completeQueueMessage: true,
            cancellationToken);
    }

    public async Task<CollectionIteratorFinalizationResult> ResumeStepFinalizationForRecoveryAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        return await ResumeStepFinalizationCoreAsync(
            context,
            record,
            completeQueueMessage: false,
            cancellationToken);
    }

    private async Task<CollectionIteratorFinalizationResult> ResumeStepFinalizationCoreAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        bool completeQueueMessage,
        CancellationToken cancellationToken)
    {
        await _stepClaimService.HydrateStepForFinalizationAsync(context, cancellationToken);

        if (ProcessStepFinalizationStageMachine.IsRecoveredWithoutQueueMessage(record.Stage))
        {
            if (completeQueueMessage)
            {
                await context.CompleteMessageAsync(cancellationToken);
            }

            return new CollectionIteratorFinalizationResult(
                record.CollectionIteratorItemCount ?? 0,
                QueuedStepCount: 0,
                DownstreamActivationElapsed: TimeSpan.Zero);
        }

        if (ProcessStepFinalizationStageMachine.IsQueueMessageCompletionPrepared(record.Stage))
        {
            if (completeQueueMessage)
            {
                await context.CompleteMessageAsync(cancellationToken);
            }
            else
            {
                await MarkRecoveredWithoutQueueMessageAsync(
                    record.Id,
                    cancellationToken);
            }

            return new CollectionIteratorFinalizationResult(
                record.CollectionIteratorItemCount ?? 0,
                QueuedStepCount: 0,
                DownstreamActivationElapsed: TimeSpan.Zero);
        }

        return record.Outcome switch
        {
            ProcessStepFinalizationOutcome.Succeeded => await RunWithRetryableErrorCheckpointAsync(
                record.Id,
                () => ContinueSuccessFinalizationAsync(
                    context,
                    record,
                    completeQueueMessage,
                    cancellationToken),
                cancellationToken),
            ProcessStepFinalizationOutcome.Failed => await RunWithRetryableErrorCheckpointAsync(
                record.Id,
                () => ContinueFailureFinalizationAsync(
                    context,
                    record,
                    completeQueueMessage,
                    cancellationToken),
                cancellationToken),
            _ => throw new InvalidOperationException(
                $"Unsupported finalization outcome '{record.Outcome}' for step {context.ProcessStepId}.")
        };
    }

    private async Task<CollectionIteratorFinalizationResult> ContinueSuccessFinalizationAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord initialRecord,
        bool completeQueueMessage,
        CancellationToken cancellationToken)
    {
        var record = await _finalizationRepository.GetAsync(
                Guid.Parse(initialRecord.Id),
                cancellationToken)
            ?? initialRecord;

        if (!HasReached(record, ProcessStepFinalizationStage.OutputsPersisted))
        {
            await PersistSuccessOutputsAsync(context, record, cancellationToken);
            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.OutputsPersisted,
                trimResultSnapshot: true,
                cancellationToken);
        }

        var activationResult = await BuildSuccessResultAsync(context, record, cancellationToken);
        CollectionIteratorFinalizationResult collectionResult = new(
            record.CollectionIteratorItemCount ?? 0,
            QueuedStepCount: 0,
            DownstreamActivationElapsed: TimeSpan.Zero);

        if (!HasReached(record, ProcessStepFinalizationStage.DownstreamActivationCompleted))
        {
            if (record.Source is ProcessStepFinalizationSource.CollectionIterator)
            {
                collectionResult = await EnqueueCollectionIteratorDownstreamStepsAsync(
                    context,
                    record,
                    cancellationToken);
            }
            else
            {
                await PlanAndPersistReadyActivationsAsync(
                    context,
                    activationResult,
                    activationResult.CompletedAt,
                    batchSize: int.MaxValue,
                    cancellationToken);
            }

            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.DownstreamActivationCompleted,
                trimResultSnapshot: false,
                cancellationToken);
        }

        _processLifecycleTelemetry.LogStepCompleted(context, activationResult);

        if (!HasReached(record, ProcessStepFinalizationStage.ProcessFinalizationCompleted))
        {
            if (await IsProcessCompletedAsync(context, cancellationToken))
            {
                if (context.PhaseMetadata is not null)
                {
                    await _phasedProcessFinalizationService.FinalizeCompletedPhaseAsync(
                        context.PhaseMetadata,
                        context.ExecutableBlueprintId,
                        context.ProcessId,
                        record.CompletedAt,
                        cancellationToken);
                }

                _processLifecycleTelemetry.LogProcessCompleted(
                    context,
                    record.CompletedAt);
            }

            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.ProcessFinalizationCompleted,
                trimResultSnapshot: false,
                cancellationToken);
        }

        if (completeQueueMessage)
        {
            await PrepareDurableFinalizationAndCompleteMessageAsync(context, record, cancellationToken);
        }
        else
        {
            await MarkRecoveredWithoutQueueMessageAsync(record.Id, cancellationToken);
        }

        return collectionResult;
    }

    private async Task<CollectionIteratorFinalizationResult> ContinueFailureFinalizationAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord initialRecord,
        bool completeQueueMessage,
        CancellationToken cancellationToken)
    {
        var record = await _finalizationRepository.GetAsync(
                Guid.Parse(initialRecord.Id),
                cancellationToken)
            ?? initialRecord;

        if (!HasReached(record, ProcessStepFinalizationStage.FailurePersisted))
        {
            await PersistFailedStepAsync(
                context,
                record.EffectiveInputData,
                record.GlobalState,
                record.ErrorMessage,
                record.CompletedAt,
                cancellationToken);

            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.FailurePersisted,
                trimResultSnapshot: true,
                cancellationToken);
        }

        var result = BuildFailureResult(context, record);

        if (!HasReached(record, ProcessStepFinalizationStage.ProcessFinalizationCompleted))
        {
            if (context.PhaseMetadata is not null)
            {
                await _phasedProcessFinalizationService.MarkPhaseFailedAsync(
                    context.PhaseMetadata,
                    context.ExecutableBlueprintId,
                    context.ProcessId,
                    record.ErrorMessage,
                    record.CompletedAt,
                    cancellationToken);
            }

            _processLifecycleTelemetry.LogStepFailed(context, result);
            _processLifecycleTelemetry.LogProcessFailed(
                context,
                record.CompletedAt,
                record.ErrorMessage);

            record = await AdvanceStageAsync(
                record.Id,
                ProcessStepFinalizationStage.ProcessFinalizationCompleted,
                trimResultSnapshot: false,
                cancellationToken);
        }

        if (completeQueueMessage)
        {
            await PrepareDurableFinalizationAndCompleteMessageAsync(context, record, cancellationToken);
        }
        else
        {
            await MarkRecoveredWithoutQueueMessageAsync(record.Id, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(record.ErrorMessage))
        {
            _logger.LogError(
                "Process step {BlueprintNodeId} for process {ProcessId} failed: {ErrorMessage}",
                context.BlueprintNodeId,
                context.ProcessId,
                record.ErrorMessage);
        }

        return new CollectionIteratorFinalizationResult(
            record.CollectionIteratorItemCount ?? 0,
            QueuedStepCount: 0,
            DownstreamActivationElapsed: TimeSpan.Zero);
    }

    private async Task PersistSuccessOutputsAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        var outputData = record.OutputData
            ?? throw new InvalidOperationException(
                $"Finalization record {record.Id} has no output data to persist.");

        await PersistSucceededStepAsync(
            context,
            record.EffectiveInputData,
            outputData,
            record.GlobalState,
            record.CompletedAt,
            cancellationToken);

        if (record.Source is ProcessStepFinalizationSource.CollectionIterator)
        {
            await ApplyCollectionIteratorExpansionAsync(
                context,
                record.CollectionIteratorRepeatedBlueprintNodeCount ?? 0,
                record.CollectionIteratorItemCount ?? 0,
                record.CompletedAt,
                cancellationToken);
        }
    }

    private async Task<CollectionIteratorFinalizationResult> EnqueueCollectionIteratorDownstreamStepsAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        var items = await ResolveCollectionIteratorItemsAsync(context, record, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var result = await PlanAndPersistCollectionIteratorActivationsAsync(
            context,
            items,
            Math.Max(1, record.CollectionIteratorBatchSize ?? 1),
            record.CompletedAt,
            cancellationToken);
        stopwatch.Stop();

        context.OutputChannelsPopulated = items.Count > 0 ? [CollectionIteratorOutputChannelName] : [];
        return new CollectionIteratorFinalizationResult(
            items.Count,
            result.ScheduledStepCount,
            stopwatch.Elapsed);
    }

    private async Task<ActivationPersistenceResult> PlanAndPersistReadyActivationsAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        DateTimeOffset timestamp,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var request = CreateActivationPlanningRequest(
            context,
            executionResult,
            joinReadinessSnapshots: null);
        var plan = _activationPlanner.PlanReadyActivations(request);

        if (plan.JoinSourceObservations.Count > 0)
        {
            var snapshots = await _activationPersistenceService.RecordJoinSourceObservationsAsync(
                plan,
                timestamp,
                cancellationToken);
            plan = _activationPlanner.PlanReadyActivations(
                request with { JoinReadinessSnapshots = snapshots });
        }

        return await _activationPersistenceService.PersistReadyActivationsAsync(
            plan,
            timestamp,
            batchSize,
            cancellationToken);
    }

    private async Task<ActivationPersistenceResult> PlanAndPersistCollectionIteratorActivationsAsync(
        StepExecutionContext context,
        IReadOnlyList<JsonElement> items,
        int batchSize,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var request = CreateCollectionIteratorActivationPlanningRequest(
            context,
            items,
            joinReadinessSnapshots: null);
        var plan = _activationPlanner.PlanCollectionIteratorItemActivations(request);

        if (plan.JoinSourceObservations.Count > 0)
        {
            var snapshots = await _activationPersistenceService.RecordJoinSourceObservationsAsync(
                plan,
                timestamp,
                cancellationToken);
            plan = _activationPlanner.PlanCollectionIteratorItemActivations(
                request with { JoinReadinessSnapshots = snapshots });
        }

        return await _activationPersistenceService.PersistReadyActivationsAsync(
            plan,
            timestamp,
            batchSize,
            cancellationToken);
    }

    private static ActivationPlanningRequest CreateActivationPlanningRequest(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        IReadOnlyDictionary<Guid, JoinReadinessSnapshot>? joinReadinessSnapshots) =>
        new(
            context.ExecutableBlueprint,
            context.BlueprintNode,
            context.ProcessId,
            context.ProcessStepId,
            executionResult,
            context.IterationContext,
            context.ProcessNodeOverrides,
            joinReadinessSnapshots);

    private static CollectionIteratorActivationPlanningRequest CreateCollectionIteratorActivationPlanningRequest(
        StepExecutionContext context,
        IReadOnlyList<JsonElement> items,
        IReadOnlyDictionary<Guid, JoinReadinessSnapshot>? joinReadinessSnapshots) =>
        new(
            context.ExecutableBlueprint,
            context.BlueprintNode,
            context.ProcessId,
            context.ProcessStepId,
            items,
            context.ProcessNodeOverrides,
            joinReadinessSnapshots);

    private async Task<IReadOnlyList<JsonElement>> ResolveCollectionIteratorItemsAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        var effectiveInputData = record.EffectiveInputData;
        if (effectiveInputData is null)
        {
            var step = await _stepClaimService.GetPersistedStepAsync(context, cancellationToken);
            effectiveInputData = step.InputData;
        }

        if (effectiveInputData is null ||
            !effectiveInputData.TryGetValue(context.InputChannelName, out var channelPayload) ||
            channelPayload.ValueKind != JsonValueKind.Object ||
            !channelPayload.TryGetProperty(CollectionInputPropertyName, out var itemsValue) ||
            itemsValue.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Collection Iterator finalization for step {context.ProcessStepId} cannot resume because persisted input does not contain an '{CollectionInputPropertyName}' array.");
        }

        return itemsValue.EnumerateArray()
            .Select(item => item.Clone())
            .ToList();
    }

    private async Task<PluginExecutionResult> BuildSuccessResultAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        var outputData = record.OutputData;
        if (outputData is null)
        {
            var step = await _stepClaimService.GetPersistedStepAsync(context, cancellationToken);
            outputData = step.OutputData;
        }

        if (outputData is null)
        {
            throw new InvalidOperationException(
                $"Finalization record {record.Id} reached {record.Stage} but no persisted output data was found.");
        }

        return new PluginExecutionResult(
            context.ProcessStepId,
            Succeeded: true,
            EffectiveInputData: record.EffectiveInputData,
            OutputData: CloneJsonMap(outputData),
            GlobalState: record.GlobalState,
            StartedAt: record.StartedAt,
            CompletedAt: record.CompletedAt,
            TimingsMs: record.TimingsMs,
            ErrorMessage: null);
    }

    private static PluginExecutionResult BuildFailureResult(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record) =>
        new(
            context.ProcessStepId,
            Succeeded: false,
            EffectiveInputData: record.EffectiveInputData,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            GlobalState: record.GlobalState,
            StartedAt: record.StartedAt,
            CompletedAt: record.CompletedAt,
            TimingsMs: record.TimingsMs,
            ErrorMessage: record.ErrorMessage);

    private async Task<bool> PersistSucceededStepAsync(
        StepExecutionContext context,
        Dictionary<string, JsonElement>? effectiveInputData,
        Dictionary<string, JsonElement> outputData,
        Dictionary<string, JsonElement>? globalState,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await _processStepRepository.CompleteProcessStepAsync(
            context.ProcessStepId,
            context.ProcessId,
            effectiveInputData is null ? null : NormalizeJsonMap(effectiveInputData),
            NormalizeJsonMap(outputData)!,
            completedAt,
            cancellationToken);

        return await UpdateProcessStateAfterSuccessAsync(
            context,
            globalState,
            completedAt,
            cancellationToken);
    }

    private async Task PersistFailedStepAsync(
        StepExecutionContext context,
        Dictionary<string, JsonElement>? effectiveInputData,
        Dictionary<string, JsonElement>? globalState,
        string? errorMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        await _processStepRepository.FailProcessStepAsync(
            context.ProcessStepId,
            context.ProcessId,
            effectiveInputData is null ? null : NormalizeJsonMap(effectiveInputData),
            errorMessage,
            failedAt,
            cancellationToken);

        await UpdateProcessStateAfterFailureAsync(
            context,
            globalState,
            failedAt,
            cancellationToken);
    }

    private async Task TryProjectProcessAsync(
        StepExecutionContext context,
        ProcessInstanceRecord<Dictionary<string, JsonElement>> process,
        CancellationToken cancellationToken)
    {
        if (_processIndexProjectionService is null)
        {
            return;
        }

        try
        {
            await _processIndexProjectionService.UpsertFromProcessAsync(
                process,
                context.BlueprintDefinitionId,
                context.ExecutableBlueprintId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Process index projection failed for process {ProcessId} during finalization.",
                context.ProcessId);
        }
    }

    private async Task<bool> IsProcessCompletedAsync(
        StepExecutionContext context,
        CancellationToken cancellationToken)
    {
        var process = await _processInstanceRepository.GetProcessStateById(
            context.ProcessId,
            context.ExecutableBlueprintId,
            cancellationToken);
        StepClaimService.EnsureExecutionProgress(context, process);
        context.ApplyProcess(process);
        return process.Status is ProcessStatus.Completed;
    }

    private async Task<bool> UpdateProcessStateAfterSuccessAsync(
        StepExecutionContext context,
        Dictionary<string, JsonElement>? globalState,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var normalizedGlobalState = globalState is null ? null : NormalizeJsonMap(globalState);
        for (var attempt = 0; attempt < MaxProcessStateUpdateAttempts; attempt++)
        {
            var process = await _processInstanceRepository.GetProcessStateById(
                context.ProcessId,
                context.ExecutableBlueprintId,
                cancellationToken);

            StepClaimService.EnsureExecutionProgress(context, process);
            if (normalizedGlobalState is not null)
            {
                process.GlobalState = normalizedGlobalState;
            }

            var wasCompleted = process.Status is ProcessStatus.Completed;
            process.ExecutionProgress.CompletedNodeIds.Add(context.BlueprintNodeId.ToString());
            process.ExecutionProgress.CompletedStepIds.Add(context.ProcessStepId.ToString());
            process.UpdatedAt = completedAt;
            if (process.Status is not ProcessStatus.Failed)
            {
                process.Status =
                    process.ExecutionProgress.TotalStepCount > 0 &&
                    process.ExecutionProgress.CompletedStepIds.Count >= process.ExecutionProgress.TotalStepCount
                        ? ProcessStatus.Completed
                        : ProcessStatus.InProgress;
            }

            if (await _processInstanceRepository.TryUpdateProcessState(process, cancellationToken))
            {
                context.ApplyProcess(process);
                await TryProjectProcessAsync(context, process, cancellationToken);
                return !wasCompleted && process.Status is ProcessStatus.Completed;
            }
        }

        throw new InvalidOperationException(
            $"Could not update process progress for process {context.ProcessId} after {MaxProcessStateUpdateAttempts} attempts.");
    }

    private async Task UpdateProcessStateAfterFailureAsync(
        StepExecutionContext context,
        Dictionary<string, JsonElement>? globalState,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        var normalizedGlobalState = globalState is null ? null : NormalizeJsonMap(globalState);
        for (var attempt = 0; attempt < MaxProcessStateUpdateAttempts; attempt++)
        {
            var process = await _processInstanceRepository.GetProcessStateById(
                context.ProcessId,
                context.ExecutableBlueprintId,
                cancellationToken);

            StepClaimService.EnsureExecutionProgress(context, process);
            if (normalizedGlobalState is not null)
            {
                process.GlobalState = normalizedGlobalState;
            }

            process.ExecutionProgress.FailedNodeIds.Add(context.BlueprintNodeId.ToString());
            process.ExecutionProgress.FailedStepIds.Add(context.ProcessStepId.ToString());
            process.Status = ProcessStatus.Failed;
            process.UpdatedAt = failedAt;

            if (await _processInstanceRepository.TryUpdateProcessState(process, cancellationToken))
            {
                context.ApplyProcess(process);
                await TryProjectProcessAsync(context, process, cancellationToken);
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not update failed process progress for process {context.ProcessId} after {MaxProcessStateUpdateAttempts} attempts.");
    }

    private async Task<bool> ApplyCollectionIteratorExpansionAsync(
        StepExecutionContext context,
        int repeatedBlueprintNodeCount,
        int itemCount,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var expansionKey = context.ProcessStepId.ToString();
        for (var attempt = 0; attempt < MaxProcessStateUpdateAttempts; attempt++)
        {
            var process = await _processInstanceRepository.GetProcessStateById(
                context.ProcessId,
                context.ExecutableBlueprintId,
                cancellationToken);

            StepClaimService.EnsureExecutionProgress(context, process);
            if (!process.ExecutionProgress.ExpandedCollectionIteratorStepIds.Add(expansionKey))
            {
                context.ApplyProcess(process);
                return process.Status is ProcessStatus.Completed;
            }

            var repeatedStepDelta = repeatedBlueprintNodeCount * itemCount - repeatedBlueprintNodeCount;
            process.ExecutionProgress.TotalStepCount = Math.Max(
                process.ExecutionProgress.CompletedStepIds.Count,
                process.ExecutionProgress.TotalStepCount + repeatedStepDelta);
            process.ExecutionProgress.TotalNodeCount = process.ExecutionProgress.TotalStepCount;
            process.UpdatedAt = timestamp;
            if (process.Status is not ProcessStatus.Failed)
            {
                process.Status =
                    process.ExecutionProgress.TotalStepCount > 0 &&
                    process.ExecutionProgress.CompletedStepIds.Count >= process.ExecutionProgress.TotalStepCount
                        ? ProcessStatus.Completed
                        : ProcessStatus.InProgress;
            }

            if (await _processInstanceRepository.TryUpdateProcessState(process, cancellationToken))
            {
                context.ApplyProcess(process);
                await TryProjectProcessAsync(context, process, cancellationToken);
                return process.Status is ProcessStatus.Completed;
            }
        }

        throw new InvalidOperationException(
            $"Could not update Collection Iterator expansion for process {context.ProcessId} after {MaxProcessStateUpdateAttempts} attempts.");
    }

    private async Task PrepareDurableFinalizationAndCompleteMessageAsync(
        StepExecutionContext context,
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        await AdvanceStageAsync(
            record.Id,
            ProcessStepFinalizationStage.QueueMessageCompletionPrepared,
            trimResultSnapshot: false,
            cancellationToken);
        await context.CompleteMessageAsync(cancellationToken);
    }

    private Task<ProcessStepFinalizationRecord> MarkRecoveredWithoutQueueMessageAsync(
        string recordId,
        CancellationToken cancellationToken) =>
        AdvanceStageAsync(
            recordId,
            ProcessStepFinalizationStage.RecoveredWithoutQueueMessage,
            trimResultSnapshot: false,
            cancellationToken);

    private async Task<ProcessStepFinalizationRecord> CreateOrGetFinalizationRecordAsync(
        StepExecutionContext context,
        PluginExecutionResult result,
        ProcessStepFinalizationSource source,
        int? collectionIteratorItemCount,
        int? collectionIteratorRepeatedBlueprintNodeCount,
        int? collectionIteratorBatchSize,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ProcessStepFinalizationRecord
        {
            id = context.ProcessStepId.ToString(),
            ProcessId = context.ProcessId.ToString(),
            BlueprintId = context.ExecutableBlueprintId.ToString(),
            BlueprintNodeId = context.BlueprintNodeId.ToString(),
            TaskDefinitionId = context.TaskDefinitionId,
            ResolvedTaskVersionId = context.ResolvedTaskVersionId,
            ResolvedTaskVersion = context.BlueprintNode.ResolvedVersion.ToString(),
            Outcome = result.Succeeded
                ? ProcessStepFinalizationOutcome.Succeeded
                : ProcessStepFinalizationOutcome.Failed,
            Source = source,
            Stage = ProcessStepFinalizationStage.ResultReceived,
            EffectiveInputData = NormalizeJsonMap(result.EffectiveInputData),
            OutputData = NormalizeJsonMap(result.OutputData),
            GlobalState = NormalizeJsonMap(result.GlobalState),
            TimingsMs = result.TimingsMs is null
                ? null
                : new Dictionary<string, double>(result.TimingsMs, StringComparer.Ordinal),
            ErrorMessage = result.ErrorMessage,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            CollectionIteratorItemCount = collectionIteratorItemCount,
            CollectionIteratorRepeatedBlueprintNodeCount = collectionIteratorRepeatedBlueprintNodeCount,
            CollectionIteratorBatchSize = collectionIteratorBatchSize,
            CreatedAt = now,
            UpdatedAt = now
        };

        var (_, persistedRecord) = await _finalizationRepository.CreateIfNotExistsAsync(
            record,
            cancellationToken);
        return persistedRecord;
    }

    private async Task<T> RunWithRetryableErrorCheckpointAsync<T>(
        string recordId,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                   ex is not ProcessStepFinalizationTransitionException)
        {
            await RecordRetryableErrorAsync(recordId, ex, cancellationToken);
            throw;
        }
    }

    private async Task RunWithRetryableErrorCheckpointAsync(
        string recordId,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        await RunWithRetryableErrorCheckpointAsync(
            recordId,
            async () =>
            {
                await action();
                return true;
            },
            cancellationToken);
    }

    private Task<ProcessStepFinalizationRecord> AdvanceStageAsync(
        string recordId,
        ProcessStepFinalizationStage stage,
        bool trimResultSnapshot,
        CancellationToken cancellationToken) =>
        UpdateFinalizationRecordAsync(
            recordId,
            record =>
            {
                if (HasReached(record, stage))
                {
                    return false;
                }

                ProcessStepFinalizationStageMachine.EnsureCanAdvance(
                    record.Outcome,
                    record.Stage,
                    stage);

                record.Stage = ProcessStepFinalizationStageMachine.NormalizeTerminalStage(stage);
                record.UpdatedAt = DateTimeOffset.UtcNow;
                record.LastErrorKind = null;
                record.LastErrorMessage = null;
                record.LastErrorAt = null;

                if (trimResultSnapshot)
                {
                    TrimResultSnapshot(record);
                }

                return true;
            },
            cancellationToken);

    private Task RecordRetryableErrorAsync(
        string recordId,
        Exception exception,
        CancellationToken cancellationToken) =>
        UpdateFinalizationRecordAsync(
            recordId,
            record =>
            {
                record.LastErrorKind = ProcessStepFinalizationErrorKind.RetryableFinalizationError;
                record.LastErrorMessage = exception.Message;
                record.LastErrorAt = DateTimeOffset.UtcNow;
                record.RetryCount++;
                record.UpdatedAt = record.LastErrorAt;
                return true;
            },
            cancellationToken);

    private async Task<ProcessStepFinalizationRecord> UpdateFinalizationRecordAsync(
        string recordId,
        Func<ProcessStepFinalizationRecord, bool> mutate,
        CancellationToken cancellationToken)
    {
        var processStepId = Guid.Parse(recordId);
        for (var attempt = 0; attempt < MaxFinalizationUpdateAttempts; attempt++)
        {
            var record = await _finalizationRepository.GetAsync(
                    processStepId,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Finalization record {recordId} was not found.");

            if (!mutate(record))
            {
                return record;
            }

            if (await _finalizationRepository.TryUpdateAsync(record, cancellationToken))
            {
                return record;
            }
        }

        throw new InvalidOperationException(
            $"Could not update finalization record {recordId} after {MaxFinalizationUpdateAttempts} attempts.");
    }

    private static bool HasReached(
        ProcessStepFinalizationRecord record,
        ProcessStepFinalizationStage stage) =>
        ProcessStepFinalizationStageMachine.HasReached(
            record.Outcome,
            record.Stage,
            stage);

    private static void TrimResultSnapshot(ProcessStepFinalizationRecord record)
    {
        record.EffectiveInputData = null;
        record.OutputData = null;
        record.GlobalState = null;
        record.TimingsMs = null;
    }

    private static Dictionary<string, JsonElement>? NormalizeJsonMap(
        Dictionary<string, JsonElement>? values) =>
        values is null
            ? null
            : CloneJsonMap(values);

    private static Dictionary<string, JsonElement> CloneJsonMap(
        IReadOnlyDictionary<string, JsonElement> values) =>
        values.ToDictionary(
            entry => entry.Key,
            entry => NormalizeJsonElement(entry.Value),
            StringComparer.Ordinal);

    private static JsonElement NormalizeJsonElement(JsonElement element)
    {
        var node = BuildNormalizedNode(element);
        var normalizedJson = node?.ToJsonString(NormalizedJsonOptions) ?? "null";
        using var normalizedDocument = JsonDocument.Parse(normalizedJson);
        return normalizedDocument.RootElement.Clone();
    }

    private static JsonNode? BuildNormalizedNode(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => BuildNormalizedObject(element),
            JsonValueKind.Array => BuildNormalizedArray(element),
            JsonValueKind.String => JsonValue.Create(element.GetString()),
            JsonValueKind.Number => BuildNormalizedNumberValue(element),
            JsonValueKind.True or JsonValueKind.False => JsonValue.Create(element.GetBoolean()),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw new InvalidOperationException($"Unsupported JSON value kind '{element.ValueKind}'.")
        };

    private static JsonObject BuildNormalizedObject(JsonElement element)
    {
        var normalizedObject = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            normalizedObject[property.Name] = BuildNormalizedNode(property.Value);
        }

        return normalizedObject;
    }

    private static JsonArray BuildNormalizedArray(JsonElement element)
    {
        var normalizedArray = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            normalizedArray.Add(BuildNormalizedNode(item));
        }

        return normalizedArray;
    }

    private static JsonValue BuildNormalizedNumberValue(JsonElement element)
    {
        if (element.TryGetInt64(out var int64Value))
        {
            return JsonValue.Create(int64Value)!;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            return JsonValue.Create(decimalValue)!;
        }

        return JsonValue.Create(element.GetDouble())!;
    }
}
