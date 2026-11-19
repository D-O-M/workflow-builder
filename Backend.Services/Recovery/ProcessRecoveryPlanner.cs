using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Models;

namespace Backend.Services.Recovery;

public static class ProcessRecoveryPlanner
{
    public static RecoveryCasePlan Build(
        ExecutableBlueprint blueprint,
        ProcessInstanceRecord process,
        IReadOnlyList<ProcessStepRecord> steps,
        IReadOnlyCollection<Guid> targetStepIds,
        IReadOnlyCollection<Guid>? continuationStepIds = null,
        IReadOnlyDictionary<Guid, string>? actions = null,
        string newWorkPolicy = "Pause",
        IReadOnlyDictionary<Guid, Guid>? reuseSources = null)
    {
        if (newWorkPolicy is not ("Pause" or "Run"))
            throw new ArgumentException("Choose Pause or Run for newly reached work.");
        var plan = new RecoveryCasePlan
        {
            ProcessId = Guid.Parse(process.Id),
            BlueprintVersionId = blueprint.Id,
            ProcessETag = process.ETag,
            NewWorkPolicy = newWorkPolicy,
            TargetStepIds = targetStepIds.Distinct().ToList()
        };
        var effective = ProcessRecoveryState.EffectiveSteps(process, steps)
            .Where(step => Guid.TryParse(step.BlueprintNodeId, out var nodeId) && blueprint.NodesById.ContainsKey(nodeId))
            .ToList();
        var byId = effective.ToDictionary(s => Guid.Parse(s.Id));
        var history = steps.ToLookup(s => s.LogicalStepId ?? s.Id);
        var effectiveByLogical = effective.ToDictionary(s => s.LogicalStepId ?? s.Id);
        var originalToCurrent = steps.Where(s => effectiveByLogical.ContainsKey(s.LogicalStepId ?? s.Id))
            .ToDictionary(s => s.Id, s => effectiveByLogical[s.LogicalStepId ?? s.Id].Id);
        if (plan.TargetStepIds.Count == 0 || plan.TargetStepIds.Any(id =>
            !byId.TryGetValue(id, out var step) || step.Status != ProcessStepStatus.Failed))
        {
            plan.Issues.Add("One or more selected Steps are no longer failed. Refresh the selection.");
            return plan;
        }
        if (effective.Any(s => s.Status is ProcessStepStatus.Pending or ProcessStepStatus.InProgress))
            plan.Issues.Add("This Process still has active work.");
        var byOccurrence = effective.ToLookup(s => (Guid.Parse(s.BlueprintNodeId), s.IterationContext?.ItemKey));

        // Trace the recorded bindings. Node identity alone cannot distinguish iterator items.
        var edges = new HashSet<(Guid Source, Guid Target)>();
        foreach (var (id, step) in byId)
        {
            foreach (var source in step.InputHydrationPlan?.Sources ?? [])
                if (originalToCurrent.TryGetValue(source.SourceProcessStepId, out var parent))
                    edges.Add((Guid.Parse(parent), id));
            if (step.IterationContext is { } iteration && originalToCurrent.TryGetValue(iteration.SourceIteratorStepId, out var iterator))
                edges.Add((Guid.Parse(iterator), id));

            // Inactive routes have no live source payload. Resolve their bindings only inside
            // the same iterator occurrence. This also supports older rows without hydration plans.
            if (step.Status != ProcessStepStatus.Skipped && step.InputHydrationPlan is not null)
                continue;
            if (!blueprint.NodesById.TryGetValue(Guid.Parse(step.BlueprintNodeId), out var node))
                continue;
            foreach (var binding in node.InboundBindings)
                foreach (var candidate in byOccurrence[(binding.SourceBlueprintNodeId, step.IterationContext?.ItemKey)])
                    edges.Add((Guid.Parse(candidate.Id), id));
        }
        var ancestors = Reach(plan.TargetStepIds, edges.ToLookup(e => e.Target, e => e.Source));
        plan.ContinuationStepIds = (continuationStepIds is { Count: > 0 } ? continuationStepIds : plan.TargetStepIds)
            .Distinct().ToList();
        if (plan.ContinuationStepIds.Any(id => !ancestors.Contains(id)))
            throw new ArgumentException("A continuation Step must precede a selected failure in the same occurrence.");
        var targets = plan.TargetStepIds.ToHashSet();
        var scope = Reach(plan.ContinuationStepIds.Concat(targets), edges.ToLookup(e => e.Source, e => e.Target),
            id => byId[id].Status == ProcessStepStatus.Failed && !targets.Contains(id));
        if (byOccurrence.Any(group => group.Skip(1).Any() && group.Any(s => scope.Contains(Guid.Parse(s.Id)))))
            plan.Issues.Add("The selected path contains multiple occurrences of a node without distinguishable item identities.");

        foreach (var step in effective.OrderBy(s => blueprint.NodesById[Guid.Parse(s.BlueprintNodeId)].ExecutionOrder)
                     .ThenBy(s => s.IterationContext?.ItemIndex).ThenBy(s => s.Id))
        {
            var id = Guid.Parse(step.Id);
            var node = blueprint.NodesById[Guid.Parse(step.BlueprintNodeId)];
            var logical = step.LogicalStepId ?? step.Id;
            var action = targets.Contains(id) ? "Run" : step.Status == ProcessStepStatus.Skipped ? "Skip" : "Reuse";
            var edited = actions?.ContainsKey(id) == true && scope.Contains(id);
            if (edited) action = actions![id];
            if (action is not ("Run" or "Reuse" or "Skip"))
                throw new ArgumentException($"Unknown recovery choice '{action}'.");
            var recorded = history[logical].Where(s => s.Status is ProcessStepStatus.Completed or ProcessStepStatus.Failed or ProcessStepStatus.Skipped)
                .OrderByDescending(s => s.CreatedAt).ToList();
            var source = step;
            if (action == "Reuse" && reuseSources is not null && reuseSources.TryGetValue(id, out var chosen))
                source = recorded.FirstOrDefault(s => Guid.Parse(s.Id) == chosen)
                    ?? throw new ArgumentException("The recorded outcome belongs to a different Step occurrence.");
            plan.Steps.Add(new RecoveryPlanStep
            {
                StepId = id, LogicalStepId = Guid.Parse(logical), BlueprintNodeId = node.Id,
                Name = node.Name, Status = step.Status.ToString(), CanStartHere = ancestors.Contains(id),
                RecoveryAction = step.RecoveryAction, ReusedFromStepId = step.ReusedFromStepId,
                InScope = scope.Contains(id), Action = action, ActionExplicit = edited,
                ReuseStepId = action == "Reuse" ? Guid.Parse(source.Id) : null,
                ItemKey = step.IterationContext?.ItemKey, Inputs = source.InputData, Outputs = source.OutputData,
                History = recorded.Select(s => new RecoveryRecordedOutcome(Guid.Parse(s.Id), s.Status.ToString(),
                    s.InputData, s.OutputData, s.RecoveryAction, s.ReusedFromStepId)).ToList()
            });
        }
        var rows = plan.Steps.ToDictionary(s => s.StepId);
        foreach (var row in plan.Steps.Where(s => s.InScope && s.Action == "Run"))
        {
            var step = byId[row.StepId];
            var node = blueprint.NodesById[row.BlueprintNodeId];
            var hydration = step.InputHydrationPlan ?? (process.Start?.InitialProcessStepId == step.Id ? process.Start.InitialInputHydrationPlan : null);
            if (hydration is null) continue;
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BlueprintInputMaterializer.ApplyInputPresence(available, hydration.InitialInput);
            foreach (var binding in hydration.Sources)
            {
                if (!originalToCurrent.TryGetValue(binding.SourceProcessStepId, out var id)) continue;
                var source = rows[Guid.Parse(id)];
                if (source.InScope && source.Action == "Skip") continue;
                if (source.InScope && source.Action == "Run")
                {
                    available.UnionWith(binding.PropertyMappings.Select(m => m.TargetProperty));
                    continue;
                }
                if (binding.InlineInput is not null)
                {
                    BlueprintInputMaterializer.ApplyInputPresence(available, binding.InlineInput);
                    continue;
                }
                if (source.Outputs?.TryGetValue(binding.SourceOutputChannelName, out var payload) != true ||
                    payload.ValueKind != JsonValueKind.Object) continue;
                var mapped = BlueprintInputMaterializer.MaterializePartialInput(payload, binding.PropertyMappings.Select(m =>
                    new ExecutableBlueprintPropertyMapping { SourceProperty = m.SourceProperty, TargetProperty = m.TargetProperty }).ToList());
                BlueprintInputMaterializer.ApplyInputPresence(available, mapped);
            }
            BlueprintInputMaterializer.ApplyInputPresence(available, hydration.DesignTimeInputOverrides);
            BlueprintInputMaterializer.ApplyInputPresence(available, hydration.RuntimeInputOverrides);
            row.MissingInputs.AddRange(node.InputChannel.RequiredProperties.Where(field => !available.Contains(field)));
        }
        AddUnreachedSteps(blueprint, process, plan, actions, byId);
        return plan;
    }

    // Show ordinary downstream work even when the original failure prevented creation of a Step.
    // The result of a refreshed branch or iterator is only known after execution.
    private static void AddUnreachedSteps(ExecutableBlueprint blueprint, ProcessInstanceRecord process,
        RecoveryCasePlan plan, IReadOnlyDictionary<Guid, string>? actions, IReadOnlyDictionary<Guid, ProcessStepRecord> existing)
    {
        var occurrences = plan.Steps.GroupBy(row => (row.BlueprintNodeId, row.ItemKey))
            .ToDictionary(group => group.Key, group => group.First());
        var pending = new Queue<RecoveryPlanStep>(plan.Steps.Where(row => row.InScope));
        while (pending.TryDequeue(out var source))
        {
            var node = blueprint.NodesById[source.BlueprintNodeId];
            if (node.NodeKind is ExecutableBlueprintNodeKind.ConditionalBranch or ExecutableBlueprintNodeKind.CollectionIterator)
                continue;
            foreach (var binding in node.OutputChannels.Values.SelectMany(channel => channel.OutboundBindings))
            {
                var target = blueprint.NodesById[binding.DestinationBlueprintNodeId];
                if (occurrences.ContainsKey((target.Id, source.ItemKey))) continue;
                var id = ProcessStepIdentity.Create(plan.ProcessId, target.Id, source.ItemKey);
                var action = actions?.GetValueOrDefault(id) ?? "Run";
                if (action is not ("Run" or "Skip"))
                    throw new ArgumentException($"'{target.Name}' has no recorded outcome to reuse.");
                var next = new RecoveryPlanStep
                {
                    StepId = id, LogicalStepId = id, BlueprintNodeId = target.Id, ItemKey = source.ItemKey,
                    Name = target.Name, Status = "NotExecuted", Action = action, InScope = true,
                    ActionExplicit = actions?.ContainsKey(id) == true
                };
                occurrences.Add((target.Id, source.ItemKey), next);
                pending.Enqueue(next);
                plan.Steps.Add(next);
            }
        }
        foreach (var row in plan.Steps.Where(row => row.InScope && row.Action == "Run"))
        {
            existing.TryGetValue(row.StepId, out var prior);
            if (prior?.InputHydrationPlan is not null ||
                prior is not null && process.Start?.InitialProcessStepId == prior.Id) continue;
            var node = blueprint.NodesById[row.BlueprintNodeId];
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node.Id == blueprint.InitialNode.Id &&
                prior?.InputData?.TryGetValue(node.InputChannel.Name, out var initial) == true && initial.ValueKind == JsonValueKind.Object)
                BlueprintInputMaterializer.ApplyInputPresence(available, initial.EnumerateObject().ToDictionary(p => p.Name, p => p.Value));
            foreach (var binding in node.InboundBindings)
            {
                if (!occurrences.TryGetValue((binding.SourceBlueprintNodeId, row.ItemKey), out var source) &&
                    !occurrences.TryGetValue((binding.SourceBlueprintNodeId, null), out source)) continue;
                if (source.InScope && source.Action == "Skip") continue;
                if (source.InScope && source.Action == "Run")
                    available.UnionWith(binding.PropertyMappings.Select(m => m.TargetProperty));
                else if (source.Outputs?.TryGetValue(binding.SourceChannelName, out var data) == true)
                    BlueprintInputMaterializer.ApplyInputPresence(available, BlueprintInputMaterializer.MaterializePartialInput(data, binding.PropertyMappings));
            }
            BlueprintInputMaterializer.ApplyInputPresence(available, node.DesignTimeInputOverrides);
            if (process.NodeOverrides.TryGetValue(node.Id.ToString(), out var overrides))
                BlueprintInputMaterializer.ApplyInputPresence(available, overrides);
            row.MissingInputs.AddRange(node.InputChannel.RequiredProperties.Where(field => !available.Contains(field)));
        }
        plan.Steps = plan.Steps.OrderBy(row => blueprint.NodesById[row.BlueprintNodeId].ExecutionOrder).ToList();
    }

    private static HashSet<Guid> Reach(IEnumerable<Guid> starting, ILookup<Guid, Guid> links, Func<Guid, bool>? exclude = null)
    {
        var seen = new HashSet<Guid>();
        var pending = new Stack<Guid>(starting);
        while (pending.TryPop(out var id))
            if (exclude?.Invoke(id) != true && seen.Add(id))
                foreach (var next in links[id]) pending.Push(next);
        return seen;
    }
}
