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
    private readonly ILogger<ActivationPersistenceService> _logger;

    public ActivationPersistenceService(
        INodeJoinStateRepository nodeJoinStateRepository,
        IProcessStepDispatcher processStepDispatcher,
        IProcessStepRepository processStepRepository,
        ILogger<ActivationPersistenceService> logger,
        IProcessRealtimeProjectionService? processRealtimeProjectionService = null)
    {
        _nodeJoinStateRepository = nodeJoinStateRepository;
        _processStepDispatcher = processStepDispatcher;
        _processStepRepository = processStepRepository;
        _processRealtimeProjectionService = processRealtimeProjectionService;
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

        _processStepDispatcher.EnsureDispatchAvailableForExecution();

        var scheduledCount = 0;
        var effectiveBatchSize = Math.Max(1, batchSize);
        var readyActivations = new List<ActivationIntent>(Math.Min(plan.Intents.Count, effectiveBatchSize));

        foreach (var intent in plan.Intents)
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
                    ReceivedSources = new Dictionary<string, InputHydrationSource>(StringComparer.Ordinal)
                    {
                        [observation.Source.BindingId] = CloneSource(observation.Source)
                    },
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                };

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
            state.ReceivedSources[observation.Source.BindingId] = CloneSource(observation.Source);
            state.UpdatedAt = timestamp;

            if (await _nodeJoinStateRepository.TryUpdateJoinStateAsync(state, cancellationToken))
            {
                return ToSnapshot(state);
            }
        }

        throw new InvalidOperationException(
            $"Could not update join state for node {observation.DestinationBlueprintNodeId} in process {observation.ProcessId}.");
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

        foreach (var step in scheduledSteps)
        {
            await _processStepDispatcher.EnsureDispatchIntentAsync(
                step,
                timestamp,
                cancellationToken);
        }

        foreach (var step in scheduledSteps)
        {
            var activation = activations.FirstOrDefault(candidate =>
                candidate.ProcessStepId.ToString().Equals(step.Id, StringComparison.Ordinal));
            if (activation is not null)
            {
                await TryProjectRealtimeStepAsync(
                    step,
                    activation.BlueprintDefinitionId,
                    activation.ExecutableBlueprintId,
                    cancellationToken);
            }
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
            await _processStepDispatcher.TryDispatchAsync(step, cancellationToken);
        }

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
        if (_processRealtimeProjectionService is null)
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
                : null);

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
