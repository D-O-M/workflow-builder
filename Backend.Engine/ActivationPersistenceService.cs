using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Services.ProcessStepQueue;

namespace Backend.Engine;

public sealed record ActivationPersistenceResult(
    int ScheduledStepCount);

public sealed class ActivationPersistenceService
{
    private const int MaxJoinStateUpdateAttempts = 10;

    private readonly INodeJoinStateRepository _nodeJoinStateRepository;
    private readonly IProcessStepDispatcher _processStepDispatcher;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessRealtimeProjectionService? _processRealtimeProjectionService;
    private readonly EngineProjectionOptions _projectionOptions;
    private readonly ILogger<ActivationPersistenceService> _logger;

    public ActivationPersistenceService(
        INodeJoinStateRepository nodeJoinStateRepository,
        IProcessStepDispatcher processStepDispatcher,
        IProcessStepRepository processStepRepository,
        ILogger<ActivationPersistenceService> logger,
        IProcessRealtimeProjectionService? processRealtimeProjectionService = null,
        EngineProjectionOptions? projectionOptions = null)
    {
        _nodeJoinStateRepository = nodeJoinStateRepository;
        _processStepDispatcher = processStepDispatcher;
        _processStepRepository = processStepRepository;
        _processRealtimeProjectionService = processRealtimeProjectionService;
        _projectionOptions = projectionOptions ?? EngineProjectionOptions.Enabled;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<Guid, JoinReadinessSnapshot>> RecordJoinSourceObservationsAsync(
        ActivationPlan plan,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (plan.JoinSourceObservations.Count == 0)
        {
            return new Dictionary<Guid, JoinReadinessSnapshot>();
        }

        var snapshots = new Dictionary<Guid, JoinReadinessSnapshot>();
        foreach (var observation in plan.JoinSourceObservations)
        {
            var snapshot = await RecordJoinSourceAsync(
                observation,
                timestamp,
                cancellationToken);
            snapshots[observation.JoinStateId] = snapshot;
        }

        return snapshots;
    }

    public async Task<ActivationPersistenceResult> PersistReadyActivationsAsync(
        ActivationPlan plan,
        DateTimeOffset timestamp,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (plan.Intents.Count == 0)
        {
            return new ActivationPersistenceResult(0);
        }

        var activeIntents = plan.Intents.Where(intent => !intent.IsSkipped).ToList();
        if (activeIntents.Count > 0)
        {
            _processStepDispatcher.EnsureDispatchAvailableForExecution();
        }
        foreach (var skipped in plan.Intents.Where(intent => intent.IsSkipped))
        {
            var (_, step) = await _processStepRepository.CreateProcessStepIfNotExists(new ProcessStepRecord
            {
                Id = skipped.ProcessStepId.ToString(),
                BlueprintNodeId = skipped.DestinationNode.Id.ToString(),
                ProcessId = skipped.ProcessId.ToString(),
                BlueprintId = skipped.ExecutableBlueprintId.ToString(),
                IterationContext = skipped.IterationContext,
                Status = ProcessStepStatus.Skipped,
                CreatedAt = timestamp,
                UpdatedAt = timestamp
            }, cancellationToken);
            if (step.Status != ProcessStepStatus.Skipped)
            {
                throw new InvalidOperationException($"Step {step.Id} was already activated on a different route.");
            }
            await TryProjectRealtimeStepAsync(step, skipped.BlueprintDefinitionId, skipped.ExecutableBlueprintId, cancellationToken);
        }

        var scheduledCount = 0;
        var effectiveBatchSize = Math.Max(1, batchSize);
        var readyActivations = new List<ActivationIntent>(Math.Min(plan.Intents.Count, effectiveBatchSize));

        foreach (var intent in activeIntents)
        {
            readyActivations.Add(intent);
            if (readyActivations.Count < effectiveBatchSize)
            {
                continue;
            }

            scheduledCount += await CreateAndDispatchStepsAsync(
                readyActivations,
                timestamp,
                cancellationToken);
            readyActivations.Clear();
        }

        if (readyActivations.Count > 0)
        {
            scheduledCount += await CreateAndDispatchStepsAsync(
                readyActivations,
                timestamp,
                cancellationToken);
        }

        return new ActivationPersistenceResult(scheduledCount);
    }

    private async Task<JoinReadinessSnapshot> RecordJoinSourceAsync(
        JoinSourceObservation observation,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var requiredBindingIds = observation.RequiredBindingIds.ToHashSet(StringComparer.Ordinal);

        for (var attempt = 0; attempt < MaxJoinStateUpdateAttempts; attempt++)
        {
            var state = await _nodeJoinStateRepository.GetJoinStateAsync(
                observation.ProcessId,
                observation.JoinStateId,
                cancellationToken);

            if (state is null)
            {
                state = new NodeJoinStateRecord
                {
                    id = observation.JoinStateId.ToString(),
                    ProcessId = observation.ProcessId.ToString(),
                    BlueprintId = observation.ExecutableBlueprintId.ToString(),
                    BlueprintNodeId = observation.DestinationBlueprintNodeId.ToString(),
                    RequiredBindingIds = requiredBindingIds,
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                };

                ApplyBindingDisposition(state, observation);
                var (created, createdState) = await _nodeJoinStateRepository.CreateJoinStateIfNotExistsAsync(
                    state,
                    cancellationToken);

                if (created)
                {
                    return ToSnapshot(state);
                }

                state = createdState;
            }

            state.RequiredBindingIds = requiredBindingIds;
            if (!ApplyBindingDisposition(state, observation))
            {
                return ToSnapshot(state);
            }
            state.UpdatedAt = timestamp;

            if (await _nodeJoinStateRepository.TryUpdateJoinStateAsync(state, cancellationToken))
            {
                return ToSnapshot(state);
            }
        }

        throw new InvalidOperationException(
            $"Could not update join state for node {observation.DestinationBlueprintNodeId} in process {observation.ProcessId}.");
    }

    private static bool ApplyBindingDisposition(NodeJoinStateRecord state, JoinSourceObservation observation)
    {
        var bindingId = observation.Source.BindingId;
        if (observation.IsInactive)
        {
            if (state.ReceivedSources.ContainsKey(bindingId))
            {
                throw new InvalidOperationException($"Join binding {bindingId} was already active.");
            }
            return state.InactiveBindingIds.Add(bindingId);
        }

        if (state.InactiveBindingIds.Contains(bindingId))
        {
            throw new InvalidOperationException($"Join binding {bindingId} was already inactive.");
        }
        return state.ReceivedSources.TryAdd(bindingId, CloneSource(observation.Source));
    }

    private async Task<int> CreateAndDispatchStepsAsync(
        IReadOnlyCollection<ActivationIntent> activations,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (activations.Count == 0)
        {
            return 0;
        }

        var processStepRecords = new List<ProcessStepRecord>(activations.Count);
        var joinStateIdsByStepId = new Dictionary<Guid, (Guid ProcessId, Guid JoinStateId)>(activations.Count);
        foreach (var activation in activations)
        {
            processStepRecords.Add(new ProcessStepRecord
            {
                Id = activation.ProcessStepId.ToString(),
                BlueprintNodeId = activation.DestinationNode.Id.ToString(),
                TaskDefinitionId = activation.DestinationNode.TaskDefinitionId,
                AllowedMajor = activation.DestinationNode.AllowedMajor,
                AllowedMinor = activation.DestinationNode.AllowedMinor,
                ProcessId = activation.ProcessId.ToString(),
                BlueprintId = activation.ExecutableBlueprintId.ToString(),
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                Status = ProcessStepStatus.Pending,
                Dispatch = ProcessStepDispatchService.CreatePendingDispatchState(timestamp),
                InputHydrationPlan = CloneInputHydrationPlan(activation.InputHydrationPlan),
                IterationContext = activation.IterationContext
            });

            if (activation.JoinStateId is Guid joinStateId)
            {
                joinStateIdsByStepId[activation.ProcessStepId] = (activation.ProcessId, joinStateId);
            }
        }

        var createdSteps = await _processStepRepository.CreateProcessStepsIfNotExists(
            processStepRecords,
            cancellationToken);
        var scheduledSteps = createdSteps
            .Where(result => result.Created || result.State.Status == ProcessStepStatus.Pending)
            .Select(result => result.State)
            .ToList();

        if (scheduledSteps.Count == 0)
        {
            return 0;
        }

        var dispatchIntentsByStepId = new Dictionary<string, ProcessStepDispatchIntentRecord>(
            scheduledSteps.Count,
            StringComparer.Ordinal);
        foreach (var step in scheduledSteps)
        {
            if (!step.IsDispatchable)
            {
                continue;
            }
            var dispatchIntent = await _processStepDispatcher.EnsureDispatchIntentAsync(
                step,
                timestamp,
                cancellationToken);
            dispatchIntentsByStepId[step.Id] = dispatchIntent;
        }

        foreach (var step in scheduledSteps)
        {
            if (!joinStateIdsByStepId.TryGetValue(Guid.Parse(step.Id), out var joinState))
            {
                continue;
            }

            await MarkJoinActivationMaterializedAsync(
                joinState.ProcessId,
                joinState.JoinStateId,
                Guid.Parse(step.Id),
                timestamp,
                cancellationToken);
        }

        foreach (var step in scheduledSteps)
        {
            if (dispatchIntentsByStepId.TryGetValue(step.Id, out var intent))
            {
                await _processStepDispatcher.TryDispatchAsync(step, intent, cancellationToken);
            }
        }

        var activationsByStepId = activations.ToDictionary(
            activation => activation.ProcessStepId.ToString(),
            StringComparer.Ordinal);
        await Task.WhenAll(scheduledSteps.Select(step =>
            activationsByStepId.TryGetValue(step.Id, out var activation)
                ? TryProjectRealtimeStepAsync(
                    step,
                    activation.BlueprintDefinitionId,
                    activation.ExecutableBlueprintId,
                    cancellationToken)
                : Task.CompletedTask));

        _logger.LogInformation(
            "Scheduled {StepCount} downstream step(s) for process {ProcessId} after receiving required inbound bindings.",
            scheduledSteps.Count,
            activations.First().ProcessId);

        return scheduledSteps.Count;
    }

    private async Task TryProjectRealtimeStepAsync(
        ProcessStepRecord step,
        int blueprintDefinitionId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        if (!_projectionOptions.SynchronousEngineUpdatesEnabled ||
            _processRealtimeProjectionService is null)
        {
            return;
        }

        try
        {
            await _processRealtimeProjectionService.UpsertFromStepAsync(
                step,
                blueprintDefinitionId,
                blueprintVersionId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Realtime projection failed for materialized activation step {ProcessStepId} in process {ProcessId}.",
                step.Id,
                step.ProcessId);
        }
    }

    private async Task MarkJoinActivationMaterializedAsync(
        Guid processId,
        Guid joinStateId,
        Guid processStepId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxJoinStateUpdateAttempts; attempt++)
        {
            var state = await _nodeJoinStateRepository.GetJoinStateAsync(
                processId,
                joinStateId,
                cancellationToken);

            if (state is null)
            {
                throw new InvalidOperationException(
                    $"Could not mark join activation {joinStateId} as materialized for process {processId} because the join state was not found.");
            }

            if (state.ActivationMaterialized)
            {
                if (string.IsNullOrWhiteSpace(state.MaterializedProcessStepId))
                {
                    state.MaterializedProcessStepId = processStepId.ToString();
                    state.UpdatedAt = timestamp;
                }
                else if (string.Equals(state.MaterializedProcessStepId, processStepId.ToString(), StringComparison.Ordinal))
                {
                    return;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Join activation {joinStateId} for process {processId} is already materialized as process step {state.MaterializedProcessStepId}, not {processStepId}.");
                }
            }
            else
            {
                state.ActivationMaterialized = true;
                state.MaterializedProcessStepId = processStepId.ToString();
                state.UpdatedAt = timestamp;
            }

            if (await _nodeJoinStateRepository.TryUpdateJoinStateAsync(state, cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not mark join activation {joinStateId} as materialized for process {processId} after {MaxJoinStateUpdateAttempts} attempts.");
    }

    private static JoinReadinessSnapshot ToSnapshot(NodeJoinStateRecord state) =>
        new(
            Guid.Parse(state.ProcessId),
            int.Parse(state.BlueprintId),
            Guid.Parse(state.Id),
            Guid.Parse(state.BlueprintNodeId),
            state.RequiredBindingIds.ToArray(),
            state.ReceivedSources.ToDictionary(
                entry => entry.Key,
                entry => CloneSource(entry.Value),
                StringComparer.Ordinal),
            state.ActivationMaterialized,
            Guid.TryParse(state.MaterializedProcessStepId, out var processStepId)
                ? processStepId
                : null,
            state.InactiveBindingIds.ToHashSet(StringComparer.Ordinal));

    private static InputHydrationPlan CloneInputHydrationPlan(InputHydrationPlan plan) =>
        new()
        {
            InputChannelName = plan.InputChannelName,
            InitialInput = CloneJsonMap(plan.InitialInput),
            Sources = plan.Sources.Select(CloneSource).ToList(),
            DesignTimeInputOverrides = CloneJsonMap(plan.DesignTimeInputOverrides),
            RuntimeInputOverrides = CloneJsonMap(plan.RuntimeInputOverrides)
        };

    private static InputHydrationSource CloneSource(InputHydrationSource source) =>
        new()
        {
            BindingId = source.BindingId,
            SourceProcessStepId = source.SourceProcessStepId,
            SourceOutputChannelName = source.SourceOutputChannelName,
            PropertyMappings = source.PropertyMappings
                .Select(mapping => new InputHydrationPropertyMapping
                {
                    SourceProperty = mapping.SourceProperty,
                    TargetProperty = mapping.TargetProperty
                })
                .ToList(),
            InlineInput = source.InlineInput is null ? null : CloneJsonMap(source.InlineInput)
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
}
