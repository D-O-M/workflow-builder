using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed class NodeActivationService
{
    private const int MaxJoinStateUpdateAttempts = 10;

    private readonly INodeJoinStateRepository _nodeJoinStateRepository;
    private readonly IProcessStepQueueWriter _processStepQueueWriter;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly ILogger<NodeActivationService> _logger;

    public NodeActivationService(
        INodeJoinStateRepository nodeJoinStateRepository,
        IProcessStepQueueWriter processStepQueueWriter,
        IProcessStepRepository processStepRepository,
        ILogger<NodeActivationService> logger)
    {
        _nodeJoinStateRepository = nodeJoinStateRepository;
        _processStepQueueWriter = processStepQueueWriter;
        _processStepRepository = processStepRepository;
        _logger = logger;
    }

    public async Task EnqueueReadyDownstreamStepsAsync(
        JobContext context,
        PluginExecutionResult executionResult,
        CancellationToken cancellationToken)
    {
        var nodesById = context.ExecutableBlueprint.NodesById;

        foreach (var outputChannel in context.BlueprintNode.OutputChannels.Values)
        {
            if (!executionResult.OutputData.ContainsKey(outputChannel.Name))
            {
                continue;
            }

            foreach (var binding in outputChannel.OutboundBindings)
            {
                if (!nodesById.TryGetValue(binding.DestinationBlueprintNodeId, out var destinationNode))
                {
                    continue;
                }

                var source = CreateHydrationSource(
                    context,
                    outputChannel,
                    binding);

                if (destinationNode.InboundBindings.Count <= 1)
                {
                    await CreateAndEnqueueStepAsync(
                        context,
                        destinationNode,
                        [source],
                        executionResult.CompletedAt,
                        context.IterationContext,
                        cancellationToken);
                    continue;
                }

                var readySources = await RecordJoinSourceAsync(
                    context,
                    destinationNode,
                    source,
                    executionResult.CompletedAt,
                    context.IterationContext,
                    cancellationToken);

                if (readySources is null)
                {
                    continue;
                }

                await CreateAndEnqueueStepAsync(
                    context,
                    destinationNode,
                    readySources,
                    executionResult.CompletedAt,
                    context.IterationContext,
                    cancellationToken);
            }
        }
    }

    public async Task<int> EnqueueCollectionIteratorItemStepsAsync(
        JobContext context,
        IReadOnlyList<JsonElement> items,
        int batchSize,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var nodesById = context.ExecutableBlueprint.NodesById;
        if (!context.BlueprintNode.OutputChannels.TryGetValue("Item", out var itemOutputChannel))
        {
            return 0;
        }

        var pendingActivations = new List<PendingActivation>(Math.Max(1, batchSize));
        var enqueuedCount = 0;
        var effectiveBatchSize = Math.Max(1, batchSize);

        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            var itemPayload = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["item"] = items[itemIndex].Clone()
                },
                JsonSerializerOptions.Web);

            foreach (var binding in itemOutputChannel.OutboundBindings)
            {
                if (!nodesById.TryGetValue(binding.DestinationBlueprintNodeId, out var destinationNode))
                {
                    continue;
                }

                var iterationContext = new ProcessStepIterationContext
                {
                    SourceIteratorStepId = context.ProcessStepId.ToString(),
                    SourceIteratorBlueprintNodeId = context.BlueprintNodeId.ToString(),
                    ItemIndex = itemIndex,
                    ItemKey = $"{context.ProcessStepId:N}:{itemIndex:D8}"
                };
                var source = CreateHydrationSource(
                    context,
                    itemOutputChannel,
                    binding,
                    BlueprintInputMaterializer.MaterializePartialInput(
                        itemPayload,
                        binding.PropertyMappings));

                if (destinationNode.InboundBindings.Count <= 1)
                {
                    pendingActivations.Add(new PendingActivation(
                        destinationNode,
                        [source],
                        iterationContext,
                        JoinStateId: null));
                }
                else
                {
                    var readySources = await RecordJoinSourceAsync(
                        context,
                        destinationNode,
                        source,
                        timestamp,
                        iterationContext,
                        cancellationToken);

                    if (readySources is not null)
                    {
                        pendingActivations.Add(new PendingActivation(
                            destinationNode,
                            readySources,
                            iterationContext,
                            CreateScopedJoinStateId(
                                context.ProcessId,
                                destinationNode.Id,
                                iterationContext)));
                    }
                }

                if (pendingActivations.Count >= effectiveBatchSize)
                {
                    enqueuedCount += await CreateAndEnqueueStepsAsync(
                        context,
                        pendingActivations,
                        timestamp,
                        cancellationToken);
                    pendingActivations.Clear();
                }
            }
        }

        if (pendingActivations.Count > 0)
        {
            enqueuedCount += await CreateAndEnqueueStepsAsync(
                context,
                pendingActivations,
                timestamp,
                cancellationToken);
        }

        return enqueuedCount;
    }

    private static InputHydrationSource CreateHydrationSource(
        JobContext context,
        ExecutableBlueprintChannel outputChannel,
        ExecutableBlueprintBinding binding,
        Dictionary<string, JsonElement>? inlineInput = null) =>
        new()
        {
            BindingId = binding.Id,
            SourceProcessStepId = context.ProcessStepId.ToString(),
            SourceOutputChannelName = outputChannel.Name,
            PropertyMappings = binding.PropertyMappings
                .Select(mapping => new InputHydrationPropertyMapping
                {
                    SourceProperty = mapping.SourceProperty,
                    TargetProperty = mapping.TargetProperty
                })
                .ToList(),
            InlineInput = inlineInput
        };

    private async Task<IReadOnlyList<InputHydrationSource>?> RecordJoinSourceAsync(
        JobContext context,
        ExecutableBlueprintNode destinationNode,
        InputHydrationSource source,
        DateTimeOffset timestamp,
        ProcessStepIterationContext? iterationContext,
        CancellationToken cancellationToken)
    {
        var requiredBindingIds = destinationNode.InboundBindings
            .Select(binding => binding.Id)
            .ToHashSet(StringComparer.Ordinal);
        var joinStateId = CreateScopedJoinStateId(
            context.ProcessId,
            destinationNode.Id,
            iterationContext);

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

    private async Task CreateAndEnqueueStepAsync(
        JobContext context,
        ExecutableBlueprintNode destinationNode,
        IReadOnlyList<InputHydrationSource> sources,
        DateTimeOffset timestamp,
        ProcessStepIterationContext? iterationContext,
        CancellationToken cancellationToken)
    {
        var activation = new PendingActivation(
            destinationNode,
            sources,
            iterationContext,
            destinationNode.InboundBindings.Count > 1
                ? CreateScopedJoinStateId(context.ProcessId, destinationNode.Id, iterationContext)
                : null);

        await CreateAndEnqueueStepsAsync(
            context,
            [activation],
            timestamp,
            cancellationToken);
    }

    private async Task<int> CreateAndEnqueueStepsAsync(
        JobContext context,
        IReadOnlyCollection<PendingActivation> pendingActivations,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (pendingActivations.Count == 0)
        {
            return 0;
        }

        var processStepRecords = new List<ProcessStepRecord>(pendingActivations.Count);
        var messagesByStepId = new Dictionary<Guid, ProcessStepInitialisationMessage>(pendingActivations.Count);
        var joinStateIdsByStepId = new Dictionary<Guid, Guid>(pendingActivations.Count);
        foreach (var activation in pendingActivations)
        {
            context.ProcessNodeOverrides.TryGetValue(
                activation.DestinationNode.Id.ToString(),
                out var runtimeOverrides);

            var processStepId = CreateScopedRuntimeIdentity(
                context.ProcessId,
                activation.DestinationNode.Id,
                activation.IterationContext);
            processStepRecords.Add(new ProcessStepRecord
            {
                Id = processStepId.ToString(),
                BlueprintNodeId = activation.DestinationNode.Id.ToString(),
                TaskDefinitionId = activation.DestinationNode.TaskDefinitionId,
                AllowedMajor = activation.DestinationNode.AllowedMajor,
                AllowedMinor = activation.DestinationNode.AllowedMinor,
                ProcessId = context.ProcessId.ToString(),
                BlueprintId = context.ExecutableBlueprintId.ToString(),
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                Status = ProcessStepStatus.Pending,
                InputHydrationPlan = new InputHydrationPlan
                {
                    InputChannelName = activation.DestinationNode.InputChannel.Name,
                    Sources = activation.Sources.ToList(),
                    DesignTimeInputOverrides = CloneJsonMap(activation.DestinationNode.DesignTimeInputOverrides),
                    RuntimeInputOverrides = CloneJsonMap(runtimeOverrides)
                },
                IterationContext = activation.IterationContext
            });

            messagesByStepId[processStepId] = new ProcessStepInitialisationMessage
            {
                ProcessId = context.ProcessId,
                ExecutableBlueprintId = context.ExecutableBlueprintId,
                ProcessStepId = processStepId,
                BlueprintNodeId = activation.DestinationNode.Id
            };

            if (activation.JoinStateId is Guid joinStateId)
            {
                joinStateIdsByStepId[processStepId] = joinStateId;
            }
        }

        var createdSteps = await _processStepRepository.CreateProcessStepsIfNotExists(
            processStepRecords,
            cancellationToken);
        var queueMessages = createdSteps
            .Where(result => result.Created || result.State.Status == ProcessStepStatus.Pending)
            .Select(result => messagesByStepId[Guid.Parse(result.State.Id)])
            .ToList();

        if (queueMessages.Count == 0)
        {
            return 0;
        }

        await _processStepQueueWriter.EnqueueBatchAsync(queueMessages, cancellationToken);

        foreach (var message in queueMessages)
        {
            if (!joinStateIdsByStepId.TryGetValue(message.ProcessStepId, out var joinStateId))
            {
                continue;
            }

            await MarkJoinStepEnqueuedAsync(
                context.ProcessId,
                joinStateId,
                message.ProcessStepId,
                timestamp,
                cancellationToken);
        }

        _logger.LogInformation(
            "Queued {StepCount} downstream step(s) for process {ProcessId} after receiving required inbound bindings.",
            queueMessages.Count,
            context.ProcessId);

        return queueMessages.Count;
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

    private static Guid CreateScopedRuntimeIdentity(
        Guid processId,
        Guid blueprintNodeId,
        ProcessStepIterationContext? iterationContext) =>
        ProcessStepIdentity.Create(processId, blueprintNodeId, iterationContext?.ItemKey);

    private static Guid CreateScopedJoinStateId(
        Guid processId,
        Guid blueprintNodeId,
        ProcessStepIterationContext? iterationContext) =>
        iterationContext is null
            ? blueprintNodeId
            : ProcessStepIdentity.Create(processId, blueprintNodeId, iterationContext.ItemKey);

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

    private sealed record PendingActivation(
        ExecutableBlueprintNode DestinationNode,
        IReadOnlyList<InputHydrationSource> Sources,
        ProcessStepIterationContext? IterationContext,
        Guid? JoinStateId);
}
