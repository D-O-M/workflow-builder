using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Models;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed record ActivationPlanningRequest(
    ExecutableBlueprint ExecutableBlueprint,
    ExecutableBlueprintNode CurrentNode,
    Guid ProcessId,
    Guid SourceProcessStepId,
    PluginExecutionResult ExecutionResult,
    ProcessStepIterationContext? IterationContext,
    IReadOnlyDictionary<string, Dictionary<string, JsonElement>> RuntimeNodeOverrides,
    IReadOnlyDictionary<Guid, JoinReadinessSnapshot>? JoinReadinessSnapshots = null);

public sealed record CollectionIteratorActivationPlanningRequest(
    ExecutableBlueprint ExecutableBlueprint,
    ExecutableBlueprintNode CurrentNode,
    Guid ProcessId,
    Guid SourceProcessStepId,
    IReadOnlyList<JsonElement> Items,
    IReadOnlyDictionary<string, Dictionary<string, JsonElement>> RuntimeNodeOverrides,
    IReadOnlyDictionary<Guid, JoinReadinessSnapshot>? JoinReadinessSnapshots = null);

public sealed record ActivationPlan(
    IReadOnlyList<ActivationIntent> Intents,
    IReadOnlyList<JoinSourceObservation> JoinSourceObservations)
{
    public static ActivationPlan Empty { get; } = new([], []);
}

public sealed record ActivationIntent(
    Guid ProcessId,
    int ExecutableBlueprintId,
    Guid ProcessStepId,
    ExecutableBlueprintNode DestinationNode,
    InputHydrationPlan InputHydrationPlan,
    ProcessStepIterationContext? IterationContext,
    Guid? JoinStateId);

public sealed record JoinSourceObservation(
    Guid ProcessId,
    int ExecutableBlueprintId,
    Guid JoinStateId,
    Guid DestinationBlueprintNodeId,
    IReadOnlyCollection<string> RequiredBindingIds,
    InputHydrationSource Source);

public sealed record JoinReadinessSnapshot(
    Guid ProcessId,
    int ExecutableBlueprintId,
    Guid JoinStateId,
    Guid DestinationBlueprintNodeId,
    IReadOnlyCollection<string> RequiredBindingIds,
    IReadOnlyDictionary<string, InputHydrationSource> ReceivedSources,
    bool StepEnqueued,
    Guid? EnqueuedProcessStepId);

public sealed class ActivationPlanner
{
    private const string CollectionIteratorOutputChannelName = "Item";

    public ActivationPlan PlanReadyActivations(ActivationPlanningRequest request)
    {
        var intents = new List<ActivationIntent>();
        var observations = new List<JoinSourceObservation>();
        var nodesById = request.ExecutableBlueprint.NodesById;

        foreach (var outputChannel in request.CurrentNode.OutputChannels.Values)
        {
            if (!request.ExecutionResult.OutputData.ContainsKey(outputChannel.Name))
            {
                continue;
            }

            foreach (var binding in outputChannel.OutboundBindings)
            {
                if (!nodesById.TryGetValue(binding.DestinationBlueprintNodeId, out var destinationNode))
                {
                    continue;
                }

                AddActivation(
                    request.ExecutableBlueprint.Id,
                    request.ProcessId,
                    request.SourceProcessStepId,
                    outputChannel,
                    binding,
                    destinationNode,
                    request.IterationContext,
                    request.RuntimeNodeOverrides,
                    request.JoinReadinessSnapshots,
                    inlineInput: null,
                    intents,
                    observations);
            }
        }

        return intents.Count == 0 && observations.Count == 0
            ? ActivationPlan.Empty
            : new ActivationPlan(intents, observations);
    }

    public ActivationPlan PlanCollectionIteratorItemActivations(
        CollectionIteratorActivationPlanningRequest request)
    {
        if (!request.CurrentNode.OutputChannels.TryGetValue(
                CollectionIteratorOutputChannelName,
                out var itemOutputChannel))
        {
            return ActivationPlan.Empty;
        }

        var intents = new List<ActivationIntent>();
        var observations = new List<JoinSourceObservation>();
        var nodesById = request.ExecutableBlueprint.NodesById;

        for (var itemIndex = 0; itemIndex < request.Items.Count; itemIndex++)
        {
            var itemPayload = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["item"] = request.Items[itemIndex].Clone()
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
                    SourceIteratorStepId = request.SourceProcessStepId.ToString(),
                    SourceIteratorBlueprintNodeId = request.CurrentNode.Id.ToString(),
                    ItemIndex = itemIndex,
                    ItemKey = $"{request.SourceProcessStepId:N}:{itemIndex:D8}"
                };

                AddActivation(
                    request.ExecutableBlueprint.Id,
                    request.ProcessId,
                    request.SourceProcessStepId,
                    itemOutputChannel,
                    binding,
                    destinationNode,
                    iterationContext,
                    request.RuntimeNodeOverrides,
                    request.JoinReadinessSnapshots,
                    BlueprintInputMaterializer.MaterializePartialInput(
                        itemPayload,
                        binding.PropertyMappings),
                    intents,
                    observations);
            }
        }

        return intents.Count == 0 && observations.Count == 0
            ? ActivationPlan.Empty
            : new ActivationPlan(intents, observations);
    }

    private static void AddActivation(
        int executableBlueprintId,
        Guid processId,
        Guid sourceProcessStepId,
        ExecutableBlueprintChannel outputChannel,
        ExecutableBlueprintBinding binding,
        ExecutableBlueprintNode destinationNode,
        ProcessStepIterationContext? iterationContext,
        IReadOnlyDictionary<string, Dictionary<string, JsonElement>> runtimeNodeOverrides,
        IReadOnlyDictionary<Guid, JoinReadinessSnapshot>? joinReadinessSnapshots,
        Dictionary<string, JsonElement>? inlineInput,
        List<ActivationIntent> intents,
        List<JoinSourceObservation> observations)
    {
        var source = CreateHydrationSource(
            sourceProcessStepId,
            outputChannel,
            binding,
            inlineInput);

        if (destinationNode.InboundBindings.Count <= 1)
        {
            intents.Add(CreateIntent(
                processId,
                executableBlueprintId,
                destinationNode,
                [source],
                iterationContext,
                runtimeNodeOverrides,
                joinStateId: null));
            return;
        }

        var joinStateId = CreateScopedJoinStateId(processId, destinationNode.Id, iterationContext);
        var requiredBindingIds = destinationNode.InboundBindings
            .Select(inboundBinding => inboundBinding.Id)
            .ToArray();
        observations.Add(new JoinSourceObservation(
            processId,
            executableBlueprintId,
            joinStateId,
            destinationNode.Id,
            requiredBindingIds,
            source));

        if (joinReadinessSnapshots is null ||
            !joinReadinessSnapshots.TryGetValue(joinStateId, out var snapshot) ||
            snapshot.StepEnqueued)
        {
            return;
        }

        var receivedSources = new Dictionary<string, InputHydrationSource>(
            snapshot.ReceivedSources,
            StringComparer.Ordinal)
        {
            [source.BindingId] = source
        };

        if (!requiredBindingIds.All(receivedSources.ContainsKey))
        {
            return;
        }

        intents.Add(CreateIntent(
            processId,
            executableBlueprintId,
            destinationNode,
            requiredBindingIds
                .OrderBy(bindingId => bindingId, StringComparer.Ordinal)
                .Select(bindingId => receivedSources[bindingId])
                .ToList(),
            iterationContext,
            runtimeNodeOverrides,
            joinStateId));
    }

    private static ActivationIntent CreateIntent(
        Guid processId,
        int executableBlueprintId,
        ExecutableBlueprintNode destinationNode,
        IReadOnlyList<InputHydrationSource> sources,
        ProcessStepIterationContext? iterationContext,
        IReadOnlyDictionary<string, Dictionary<string, JsonElement>> runtimeNodeOverrides,
        Guid? joinStateId)
    {
        runtimeNodeOverrides.TryGetValue(destinationNode.Id.ToString(), out var runtimeOverrides);

        return new ActivationIntent(
            processId,
            executableBlueprintId,
            CreateScopedRuntimeIdentity(processId, destinationNode.Id, iterationContext),
            destinationNode,
            new InputHydrationPlan
            {
                InputChannelName = destinationNode.InputChannel.Name,
                Sources = CloneSources(sources),
                DesignTimeInputOverrides = CloneJsonMap(destinationNode.DesignTimeInputOverrides) ?? new(StringComparer.Ordinal),
                RuntimeInputOverrides = CloneJsonMap(runtimeOverrides) ?? new(StringComparer.Ordinal)
            },
            iterationContext,
            joinStateId);
    }

    private static InputHydrationSource CreateHydrationSource(
        Guid sourceProcessStepId,
        ExecutableBlueprintChannel outputChannel,
        ExecutableBlueprintBinding binding,
        Dictionary<string, JsonElement>? inlineInput = null) =>
        new()
        {
            BindingId = binding.Id,
            SourceProcessStepId = sourceProcessStepId.ToString(),
            SourceOutputChannelName = outputChannel.Name,
            PropertyMappings = binding.PropertyMappings
                .Select(mapping => new InputHydrationPropertyMapping
                {
                    SourceProperty = mapping.SourceProperty,
                    TargetProperty = mapping.TargetProperty
                })
                .ToList(),
            InlineInput = CloneJsonMap(inlineInput)
        };

    private static List<InputHydrationSource> CloneSources(IReadOnlyList<InputHydrationSource> sources) =>
        sources.Select(source => new InputHydrationSource
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
            InlineInput = CloneJsonMap(source.InlineInput)
        }).ToList();

    private static Dictionary<string, JsonElement>? CloneJsonMap(
        IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null)
        {
            return null;
        }

        return values.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Clone(),
            StringComparer.Ordinal);
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
}
