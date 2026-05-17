using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Backend.Services.ProcessStepQueue;

namespace Backend.Engine;

public sealed record ActivationPersistenceResult(
    int ScheduledStepCount);

public sealed class ActivationPersistenceService
{
    private const int MaxJoinStateUpdateAttempts = 10;

    private readonly ActivationPlanner _activationPlanner;
    private readonly INodeJoinStateRepository _nodeJoinStateRepository;
    private readonly ProcessStepDispatchService _processStepDispatchService;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly ILogger<ActivationPersistenceService> _logger;

    public ActivationPersistenceService(
        ActivationPlanner activationPlanner,
        INodeJoinStateRepository nodeJoinStateRepository,
        ProcessStepDispatchService processStepDispatchService,
        IProcessStepRepository processStepRepository,
        ILogger<ActivationPersistenceService> logger)
    {
        _activationPlanner = activationPlanner;
        _nodeJoinStateRepository = nodeJoinStateRepository;
        _processStepDispatchService = processStepDispatchService;
        _processStepRepository = processStepRepository;
        _logger = logger;
    }

    public Task<ActivationPersistenceResult> PersistReadyActivationsAsync(
        StepExecutionContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken) =>
        PersistReadyActivationsAsync(
            context,
            _activationPlanner.PlanReadyActivations(context, executionResult),
            executionResult.CompletedAt,
            batchSize: int.MaxValue,
            cancellationToken);

    public Task<ActivationPersistenceResult> PersistCollectionIteratorItemActivationsAsync(
        StepExecutionContext context,
        IReadOnlyList<JsonElement> items,
        int batchSize,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken) =>
        PersistReadyActivationsAsync(
            context,
            _activationPlanner.PlanCollectionIteratorItemActivations(context, items),
            timestamp,
            Math.Max(1, batchSize),
            cancellationToken);

    public async Task<ActivationPersistenceResult> PersistReadyActivationsAsync(
        StepExecutionContext context,
        ActivationPlan plan,
        DateTimeOffset timestamp,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (plan.Candidates.Count == 0)
        {
            return new ActivationPersistenceResult(0);
        }

        var readyActivations = new List<ActivationCandidate>(Math.Min(plan.Candidates.Count, batchSize));
        var scheduledCount = 0;
        var effectiveBatchSize = Math.Max(1, batchSize);

        foreach (var candidate in plan.Candidates)
        {
            if (candidate.DestinationNode.InboundBindings.Count <= 1)
            {
                readyActivations.Add(candidate);
            }
            else
            {
                var readySources = await RecordJoinSourceAsync(
                    context,
                    candidate,
                    timestamp,
                    cancellationToken);

                if (readySources is not null)
                {
                    readyActivations.Add(_activationPlanner.WithSources(
                        context,
                        candidate,
                        readySources));
                }
            }

            if (readyActivations.Count >= effectiveBatchSize)
            {
                scheduledCount += await CreateAndDispatchStepsAsync(
                    context,
                    readyActivations,
                    timestamp,
                    cancellationToken);
                readyActivations.Clear();
            }
        }

        if (readyActivations.Count > 0)
        {
            scheduledCount += await CreateAndDispatchStepsAsync(
                context,
                readyActivations,
                timestamp,
                cancellationToken);
        }

        return new ActivationPersistenceResult(scheduledCount);
    }

    private async Task<IReadOnlyList<InputHydrationSource>?> RecordJoinSourceAsync(
        StepExecutionContext context,
        ActivationCandidate candidate,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var destinationNode = candidate.DestinationNode;
        var source = candidate.Sources.Single();
        var requiredBindingIds = destinationNode.InboundBindings
            .Select(binding => binding.Id)
            .ToHashSet(StringComparer.Ordinal);
        var joinStateId = candidate.JoinStateId
            ?? throw new InvalidOperationException(
                $"Activation for node {destinationNode.Id} has no join state id.");

        for (var attempt = 0; attempt < MaxJoinStateUpdateAttempts; attempt++)
        {
            var state = await _nodeJoinStateRepository.GetJoinStateAsync(
                context.ProcessId,
                joinStateId,
                cancellationToken);

            if (state is null)
            {
                state = new NodeJoinStateRecord
                {
                    id = joinStateId.ToString(),
                    ProcessId = context.ProcessId.ToString(),
                    BlueprintId = context.ExecutableBlueprintId.ToString(),
                    BlueprintNodeId = destinationNode.Id.ToString(),
                    RequiredBindingIds = requiredBindingIds,
                    ReceivedSources = new Dictionary<string, InputHydrationSource>(StringComparer.Ordinal)
                    {
                        [source.BindingId] = source
                    },
                    CreatedAt = timestamp,
                    UpdatedAt = timestamp
                };

                var (created, createdState) = await _nodeJoinStateRepository.CreateJoinStateIfNotExistsAsync(
                    state,
                    cancellationToken);

                if (created)
                {
                    return null;
                }

                state = createdState;
            }

            state.RequiredBindingIds = requiredBindingIds;
            state.ReceivedSources[source.BindingId] = source;
            state.UpdatedAt = timestamp;

            var ready = !state.StepEnqueued &&
                requiredBindingIds.All(bindingId => state.ReceivedSources.ContainsKey(bindingId));
            var readySources = ready
                ? state.ReceivedSources
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => entry.Value)
                    .ToList()
                : null;

            if (await _nodeJoinStateRepository.TryUpdateJoinStateAsync(state, cancellationToken))
            {
                return readySources;
            }
        }

        throw new InvalidOperationException(
            $"Could not update join state for node {destinationNode.Id} in process {context.ProcessId}.");
    }

    private async Task<int> CreateAndDispatchStepsAsync(
        StepExecutionContext context,
        IReadOnlyCollection<ActivationCandidate> activations,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (activations.Count == 0)
        {
            return 0;
        }

        var processStepRecords = new List<ProcessStepRecord>(activations.Count);
        var joinStateIdsByStepId = new Dictionary<Guid, Guid>(activations.Count);
        foreach (var activation in activations)
        {
            context.ProcessNodeOverrides.TryGetValue(
                activation.DestinationNode.Id.ToString(),
                out var runtimeOverrides);

            processStepRecords.Add(new ProcessStepRecord
            {
                Id = activation.ProcessStepId.ToString(),
                BlueprintNodeId = activation.DestinationNode.Id.ToString(),
                TaskDefinitionId = activation.DestinationNode.TaskDefinitionId,
                AllowedMajor = activation.DestinationNode.AllowedMajor,
                AllowedMinor = activation.DestinationNode.AllowedMinor,
                ProcessId = context.ProcessId.ToString(),
                BlueprintId = context.ExecutableBlueprintId.ToString(),
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                Status = ProcessStepStatus.Pending,
                Dispatch = ProcessStepDispatchService.CreatePendingDispatchState(timestamp),
                InputHydrationPlan = new InputHydrationPlan
                {
                    InputChannelName = activation.DestinationNode.InputChannel.Name,
                    Sources = activation.Sources.ToList(),
                    DesignTimeInputOverrides = CloneJsonMap(activation.DestinationNode.DesignTimeInputOverrides),
                    RuntimeInputOverrides = CloneJsonMap(runtimeOverrides)
                },
                IterationContext = activation.IterationContext
            });

            if (activation.JoinStateId is Guid joinStateId)
            {
                joinStateIdsByStepId[activation.ProcessStepId] = joinStateId;
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
            if (!joinStateIdsByStepId.TryGetValue(Guid.Parse(step.Id), out var joinStateId))
            {
                continue;
            }

            await MarkJoinStepEnqueuedAsync(
                context.ProcessId,
                joinStateId,
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
            context.ProcessId);

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
