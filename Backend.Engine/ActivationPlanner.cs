using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Models;
using Backend.Plugins.Communication;

namespace Backend.Engine;

public sealed record ActivationPlan(
    IReadOnlyList<ActivationCandidate> Candidates)
{
    public static ActivationPlan Empty { get; } = new([]);
}

public sealed record ActivationCandidate(
    Guid ProcessStepId,
    ExecutableBlueprintNode DestinationNode,
    IReadOnlyList<InputHydrationSource> Sources,
    ProcessStepIterationContext? IterationContext,
    Guid? JoinStateId);

public sealed class ActivationPlanner
{
    private const string CollectionIteratorOutputChannelName = "Item";

    public ActivationPlan PlanReadyActivations(
        StepExecutionContext context,
        PluginExecutionResult executionResult)
    {
        var nodesById = context.ExecutableBlueprint.NodesById;
        var candidates = new List<ActivationCandidate>();

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

                candidates.Add(CreateCandidate(
                    context,
                    destinationNode,
                    [CreateHydrationSource(context, outputChannel, binding)],
                    context.IterationContext));
            }
        }

        return candidates.Count == 0
            ? ActivationPlan.Empty
            : new ActivationPlan(candidates);
    }

    public ActivationPlan PlanCollectionIteratorItemActivations(
        StepExecutionContext context,
        IReadOnlyList<JsonElement> items)
    {
        if (!context.BlueprintNode.OutputChannels.TryGetValue(
                CollectionIteratorOutputChannelName,
                out var itemOutputChannel))
        {
            return ActivationPlan.Empty;
        }

        var nodesById = context.ExecutableBlueprint.NodesById;
        var candidates = new List<ActivationCandidate>();

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

                candidates.Add(CreateCandidate(
                    context,
                    destinationNode,
                    [CreateHydrationSource(
                        context,
                        itemOutputChannel,
                        binding,
                        BlueprintInputMaterializer.MaterializePartialInput(
                            itemPayload,
                            binding.PropertyMappings))],
                    iterationContext));
            }
        }

        return candidates.Count == 0
            ? ActivationPlan.Empty
            : new ActivationPlan(candidates);
    }

    public ActivationCandidate WithSources(
        StepExecutionContext context,
        ActivationCandidate candidate,
        IReadOnlyList<InputHydrationSource> sources) =>
        candidate with
        {
            Sources = sources,
            ProcessStepId = CreateScopedRuntimeIdentity(
                context.ProcessId,
                candidate.DestinationNode.Id,
                candidate.IterationContext),
            JoinStateId = candidate.DestinationNode.InboundBindings.Count > 1
                ? CreateScopedJoinStateId(
                    context.ProcessId,
                    candidate.DestinationNode.Id,
                    candidate.IterationContext)
                : null
        };

    private static ActivationCandidate CreateCandidate(
        StepExecutionContext context,
        ExecutableBlueprintNode destinationNode,
        IReadOnlyList<InputHydrationSource> sources,
        ProcessStepIterationContext? iterationContext) =>
        new(
            CreateScopedRuntimeIdentity(context.ProcessId, destinationNode.Id, iterationContext),
            destinationNode,
            sources,
            iterationContext,
            destinationNode.InboundBindings.Count > 1
                ? CreateScopedJoinStateId(context.ProcessId, destinationNode.Id, iterationContext)
                : null);

    private static InputHydrationSource CreateHydrationSource(
        StepExecutionContext context,
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
