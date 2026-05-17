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
    private readonly ProcessStepDispatchService _processStepDispatchService;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly ILogger<ActivationPersistenceService> _logger;

    public ActivationPersistenceService(
        INodeJoinStateRepository nodeJoinStateRepository,
        ProcessStepDispatchService processStepDispatchService,
        IProcessStepRepository processStepRepository,
        ILogger<ActivationPersistenceService> logger)
    {
        _nodeJoinStateRepository = nodeJoinStateRepository;
        _processStepDispatchService = processStepDispatchService;
        _processStepRepository = processStepRepository;
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
            if (!joinStateIdsByStepId.TryGetValue(Guid.Parse(step.Id), out var joinState))
            {
                continue;
            }

            await MarkJoinStepEnqueuedAsync(
                joinState.ProcessId,
                joinState.JoinStateId,
                Guid.Parse(step.Id),
                timestamp,
                cancellationToken);
        }

        foreach (var step in scheduledSteps)
        {
            await _processStepDispatchService.TryDispatchAsync(step, cancellationToken);
        }

        _logger.LogInformation(
            "Scheduled {StepCount} downstream step(s) for process {ProcessId} after receiving required inbound bindings.",
            scheduledSteps.Count,
            activations.First().ProcessId);

        return scheduledSteps.Count;
    }

    private async Task MarkJoinStepEnqueuedAsync(
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

            if (state is null || state.StepEnqueued)
            {
                return;
            }

            state.StepEnqueued = true;
            state.EnqueuedProcessStepId = processStepId.ToString();
            state.UpdatedAt = timestamp;

            if (await _nodeJoinStateRepository.TryUpdateJoinStateAsync(state, cancellationToken))
            {
                return;
            }
        }
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
            state.StepEnqueued,
            Guid.TryParse(state.EnqueuedProcessStepId, out var processStepId)
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
