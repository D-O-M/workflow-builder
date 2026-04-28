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
                        cancellationToken);
                    continue;
                }

                var readySources = await RecordJoinSourceAsync(
                    context,
                    destinationNode,
                    source,
                    executionResult.CompletedAt,
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
                    cancellationToken);
            }
        }
    }

    private static InputHydrationSource CreateHydrationSource(
        JobContext context,
        ExecutableBlueprintChannel outputChannel,
        ExecutableBlueprintBinding binding) =>
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
                .ToList()
        };

    private async Task<IReadOnlyList<InputHydrationSource>?> RecordJoinSourceAsync(
        JobContext context,
        ExecutableBlueprintNode destinationNode,
        InputHydrationSource source,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var requiredBindingIds = destinationNode.InboundBindings
            .Select(binding => binding.Id)
            .ToHashSet(StringComparer.Ordinal);

        for (var attempt = 0; attempt < MaxJoinStateUpdateAttempts; attempt++)
        {
            var state = await _nodeJoinStateRepository.GetJoinStateAsync(
                context.ProcessId,
                destinationNode.Id,
                cancellationToken);

            if (state is null)
            {
                state = new NodeJoinStateRecord
                {
                    id = destinationNode.Id.ToString(),
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
        CancellationToken cancellationToken)
    {
        context.ProcessNodeOverrides.TryGetValue(destinationNode.Id.ToString(), out var runtimeOverrides);

        var processStepId = ProcessStepIdentity.Create(context.ProcessId, destinationNode.Id);
        await _processStepRepository.CreateProcessStepIfNotExists(
            new ProcessStepRecord
            {
                Id = processStepId.ToString(),
                BlueprintNodeId = destinationNode.Id.ToString(),
                TaskDefinitionId = destinationNode.TaskDefinitionId,
                AllowedMajor = destinationNode.AllowedMajor,
                AllowedMinor = destinationNode.AllowedMinor,
                ProcessId = context.ProcessId.ToString(),
                BlueprintId = context.ExecutableBlueprintId.ToString(),
                CreatedAt = timestamp,
                UpdatedAt = timestamp,
                Status = ProcessStepStatus.Pending,
                InputHydrationPlan = new InputHydrationPlan
                {
                    InputChannelName = destinationNode.InputChannel.Name,
                    Sources = sources.ToList(),
                    DesignTimeInputOverrides = CloneJsonMap(destinationNode.DesignTimeInputOverrides),
                    RuntimeInputOverrides = CloneJsonMap(runtimeOverrides)
                }
            },
            cancellationToken);

        await _processStepQueueWriter.EnqueueAsync(
            new ProcessStepInitialisationMessage
            {
                ProcessId = context.ProcessId,
                ExecutableBlueprintId = context.ExecutableBlueprintId,
                ProcessStepId = processStepId,
                BlueprintNodeId = destinationNode.Id
            },
            cancellationToken);

        if (destinationNode.InboundBindings.Count > 1)
        {
            await MarkJoinStepEnqueuedAsync(
                context.ProcessId,
                destinationNode.Id,
                processStepId,
                timestamp,
                cancellationToken);
        }

        _logger.LogInformation(
            "Queued downstream node {BlueprintNodeId} for process {ProcessId} after receiving required inbound bindings.",
            destinationNode.Id,
            context.ProcessId);
    }

    private async Task MarkJoinStepEnqueuedAsync(
        Guid processId,
        Guid destinationNodeId,
        Guid processStepId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxJoinStateUpdateAttempts; attempt++)
        {
            var state = await _nodeJoinStateRepository.GetJoinStateAsync(
                processId,
                destinationNodeId,
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
