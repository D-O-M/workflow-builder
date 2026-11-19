using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Models;
using Backend.Services.ProcessStepQueue;

namespace Backend.Dispatcher;

// Recalculate the next ready occurrences from immutable source attempts and durable results.
// A join sees one observation per binding and item in this recovery, independent of older join records.
internal sealed class ProcessRecoveryGraph(
    Guid recoveryId, int revision, RecoveryCasePlan plan, ExecutableBlueprint blueprint,
    ProcessInstanceRecord process, IReadOnlyList<ProcessStepRecord> history)
{
    private readonly Dictionary<string, ProcessStepRecord> _byId = history.ToDictionary(step => step.Id);
    private readonly ILookup<Guid, RecoveryPlanStep> _rows = plan.Steps.ToLookup(step => step.BlueprintNodeId);
    private readonly Dictionary<Guid, List<BindingObservation>> _inbound = [];
    private readonly Dictionary<string, JsonElement[]> _iteratorItems = [];
    private readonly Dictionary<(string?, string?), ExpansionComparison> _expansions = [];
    public List<ProcessStepRecord> Ready { get; } = [];
    public List<ProcessStepRecord> Running { get; } = [];
    public List<ProcessStepRecord> Held { get; } = [];
    public List<ProcessStepRecord> Failed { get; } = [];

    public void Evaluate()
    {
        foreach (var node in OrderedNodes())
        {
            var incoming = _inbound.GetValueOrDefault(node.Id) ?? [];
            // Admission rejects ambiguous occurrences that can contribute to the reviewed work.
            var rows = _rows[node.Id].GroupBy(row => row.ItemKey ?? "").ToDictionary(group => group.Key, group => group.First());
            var byScope = incoming.ToLookup(observation => observation.Iteration?.ItemKey ?? "");
            foreach (var itemKey in rows.Keys.Concat(byScope.Select(group => group.Key)).Distinct())
            {
                rows.TryGetValue(itemKey, out var row);
                var edges = byScope[itemKey].ToList();
                var prior = row is null ? null : _byId.GetValueOrDefault(row.StepId.ToString());
                if (prior is null && (edges.Count == 0 || edges.All(e => e.State == Flow.Inactive)))
                    continue;
                var iteration = edges.FirstOrDefault()?.Iteration ?? prior?.IterationContext;
                var logicalId = row?.LogicalStepId ?? ProcessStepIdentity.Create(plan.ProcessId, node.Id, iteration?.ItemKey);
                var affected = row?.InScope == true || edges.Any(edge => edge.Affected);
                var routeChanged = edges.Any(edge => edge.NewPath && edge.State == Flow.Active);
                var oldRouteInactive = prior?.Status == ProcessStepStatus.Skipped && prior.RecoveryAction != "Skip";
                var needsDecision = row?.InScope == true || (affected && (row is null || oldRouteInactive));
                if (!needsDecision)
                {
                    if (prior is not null)
                        Publish(node, logicalId, iteration, prior, prior, PriorFlow(prior), false);
                    continue;
                }

                // A pending source may still change route or supply a required field.
                if (edges.Any(edge => edge.State == Flow.Waiting))
                {
                    Publish(node, logicalId, iteration, prior, null, Flow.Waiting, true);
                    continue;
                }

                var active = edges.Any(edge => edge.State is Flow.Active or Flow.Bypass);
                var inactive = edges.Count > 0 && !active;
                var action = row?.Action ?? "Run";
                string? detail = null;
                if (inactive)
                    action = "Inactive";
                else if ((routeChanged || prior is null && edges.Any(edge => edge.NewItem)) &&
                         (prior is null || oldRouteInactive) && row?.ActionExplicit != true)
                {
                    action = plan.NewWorkPolicy == "Run" && !edges.Any(edge => edge.AmbiguousItem) ? "Run" : "Review";
                    detail = action == "Review" ? $"Review newly reached Step '{node.Name}'." : null;
                }
                else if (oldRouteInactive && row?.ActionExplicit != true && active)
                    action = "Run";

                // An omitted iterator occurrence has no incoming bindings on the refreshed expansion.
                if (prior?.IterationContext is { } oldIteration && edges.Count == 0 &&
                    _rows[Guid.Parse(oldIteration.SourceIteratorBlueprintNodeId)].Any(r => r.InScope))
                    action = "Inactive";

                var attemptId = ProcessStepIdentity.Create(recoveryId, logicalId, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (_byId.TryGetValue(attemptId.ToString(), out var current))
                {
                    if (current.Status is ProcessStepStatus.Pending or ProcessStepStatus.InProgress)
                        Running.Add(current);
                    else if (current.RecoveryAction == "Review")
                        Held.Add(current);
                    else if (current.Status == ProcessStepStatus.Failed)
                        Failed.Add(current);
                    var flow = current.Status == ProcessStepStatus.Failed ? Flow.Waiting : PriorFlow(current);
                    Publish(node, logicalId, iteration, prior, current, flow, true);
                    continue;
                }
                if (inactive && row is null)
                {
                    Publish(node, logicalId, iteration, prior, null, Flow.Inactive, true);
                    continue;
                }
                var reused = row?.ReuseStepId is Guid reuseId ? _byId.GetValueOrDefault(reuseId.ToString()) : prior;
                if (action == "Reuse" && (reused is null || (reused.LogicalStepId ?? reused.Id) != logicalId.ToString()))
                {
                    action = "Review";
                    detail = $"The selected recorded outcome for '{node.Name}' is unavailable.";
                }
                var hydration = BuildHydration(node, prior, edges, out var missing);
                if (action == "Run" && missing.Count > 0)
                {
                    action = "Review";
                    detail = $"'{node.Name}' requires {string.Join(", ", missing)}.";
                }
                var now = DateTimeOffset.UtcNow;
                Ready.Add(new ProcessStepRecord
                {
                    Id = attemptId.ToString(), LogicalStepId = logicalId.ToString(),
                    RecoveryId = recoveryId, RecoveryRevision = revision, RecoveryAction = action,
                    RecoverySequence = process.Recovery!.Sequence,
                    RecoverySourceStepId = prior?.Id,
                    ReusedFromStepId = action == "Reuse" ? reused!.Id : null,
                    ProcessId = process.Id, BlueprintId = process.BlueprintId, BlueprintNodeId = node.Id.ToString(),
                    TaskDefinitionId = node.TaskDefinitionId, AllowedMajor = node.AllowedMajor, AllowedMinor = node.AllowedMinor,
                    ResolvedTaskVersionId = action == "Reuse" ? reused!.ResolvedTaskVersionId : null,
                    ResolvedTaskVersion = action == "Reuse" ? reused!.ResolvedTaskVersion : null,
                    CreatedAt = now, UpdatedAt = now, IterationContext = iteration,
                    InputHydrationPlan = hydration,
                    InputData = action == "Reuse" ? reused!.InputData : null,
                    OutputData = action == "Reuse" ? reused!.OutputData : null,
                    Status = action switch { "Run" => ProcessStepStatus.Pending, "Reuse" => ProcessStepStatus.Completed,
                        "Review" => ProcessStepStatus.Failed, _ => ProcessStepStatus.Skipped },
                    ErrorMessage = detail, FailedAt = action == "Review" ? now : null,
                    CompletedAt = action is "Reuse" or "Skip" or "Inactive" ? now : null,
                    Dispatch = action == "Run" ? ProcessStepDispatchService.CreatePendingDispatchState(now) : null
                });
                Publish(node, logicalId, iteration, prior, null, Flow.Waiting, true);
            }
        }
    }

    internal static string? AmbiguousOccurrence(ExecutableBlueprint graph, RecoveryCasePlan plan)
    {
        foreach (var group in plan.Steps.GroupBy(row => (row.BlueprintNodeId, row.ItemKey)).Where(g => g.Count() > 1))
        {
            var touched = plan.Steps.Where(r => r.InScope && (r.ItemKey is null || group.Key.ItemKey is null || r.ItemKey == group.Key.ItemKey));
            // A legacy duplicate is relevant when it can reach a node touched by these actions,
            // including a join beyond the original failure which has not yet been materialized.
            var affected = Descendants(graph, touched.Select(r => r.BlueprintNodeId));
            if (affected.Overlaps(Descendants(graph, [group.Key.BlueprintNodeId])))
                return $"Several recorded occurrences of '{group.First().Name}' share the same item identity.";
        }
        return null;
    }

    private static HashSet<Guid> Descendants(ExecutableBlueprint blueprint, IEnumerable<Guid> starting)
    {
        var pending = new Stack<Guid>(starting);
        var visited = new HashSet<Guid>();
        while (pending.TryPop(out var id))
        {
            if (!visited.Add(id) || !blueprint.NodesById.TryGetValue(id, out var node)) continue;
            foreach (var edge in node.OutputChannels.Values.SelectMany(channel => channel.OutboundBindings))
                pending.Push(edge.DestinationBlueprintNodeId);
        }
        return visited;
    }

    private static Flow PriorFlow(ProcessStepRecord step) => step.Status switch
    {
        ProcessStepStatus.Completed => Flow.Active,
        ProcessStepStatus.Skipped when step.RecoveryAction == "Skip" => Flow.Bypass,
        ProcessStepStatus.Skipped => Flow.Inactive,
        _ => Flow.Waiting
    };

    private void Publish(ExecutableBlueprintNode node, Guid logicalId, ProcessStepIterationContext? iteration,
        ProcessStepRecord? prior, ProcessStepRecord? current, Flow flow, bool affected)
    {
        if (node.NodeKind == ExecutableBlueprintNodeKind.CollectionIterator)
        {
            PublishItems(node, logicalId, prior, current, flow, affected);
            return;
        }
        var baseline = node.NodeKind == ExecutableBlueprintNodeKind.PluginTask ? LastCompletedAttempt(prior) : prior;
        foreach (var output in node.OutputChannels.Values)
        {
            JsonElement payload = default;
            var hasOutput = current?.OutputData?.TryGetValue(output.Name, out payload) == true;
            var state = flow == Flow.Active && !hasOutput ? Flow.Inactive : flow;
            // An accepted failure has no saved result. Explicit later work can still use other inputs.
            if (state == Flow.Inactive && flow == Flow.Active && current?.RecoveryAction == "Reuse"
                && current.OutputData is not { Count: > 0 })
                state = Flow.Bypass;
            // The first success of a failed Plugin resumes ordinary downstream work. Once a
            // success was recorded, a newly populated output channel is a change of route.
            var newPath = affected && state == Flow.Active &&
                (node.NodeKind == ExecutableBlueprintNodeKind.ConditionalBranch || baseline is not null) &&
                baseline?.OutputData?.ContainsKey(output.Name) != true;
            foreach (var binding in output.OutboundBindings)
                Observe(new(binding, current, iteration, state, affected, hasOutput ? payload : null, false, newPath, false, false));
        }
    }

    private ProcessStepRecord? LastCompletedAttempt(ProcessStepRecord? attempt)
    {
        while (attempt is not null)
        {
            if (attempt.Status == ProcessStepStatus.Completed && attempt.RecoveryAction != "Reuse")
                return attempt;
            var source = attempt.ReusedFromStepId ?? attempt.RecoverySourceStepId;
            attempt = source is null ? null : _byId.GetValueOrDefault(source);
        }
        return null;
    }

    private void PublishItems(ExecutableBlueprintNode node, Guid logicalId, ProcessStepRecord? prior,
        ProcessStepRecord? current, Flow flow, bool affected)
    {
        if (!node.OutputChannels.TryGetValue("Item", out var channel)) return;
        if (flow == Flow.Bypass || flow == Flow.Active && AcceptedWithoutExpansion(current))
        {
            // A skipped iterator supplies no items. Existing occurrences can still be explicitly
            // selected when their own required inputs come from elsewhere.
            foreach (var binding in channel.OutboundBindings)
            foreach (var row in _rows[binding.DestinationBlueprintNodeId])
            {
                var scope = _byId.GetValueOrDefault(row.StepId.ToString())?.IterationContext;
                if (scope is null || scope.SourceIteratorBlueprintNodeId != node.Id.ToString()) continue;
                var source = _byId.GetValueOrDefault(scope.SourceIteratorStepId);
                if ((source?.LogicalStepId ?? scope.SourceIteratorStepId) == logicalId.ToString())
                    Observe(new(binding, current, scope, Flow.Bypass, affected, null, true, false, false, false));
            }
            return;
        }
        var comparison = CompareExpansion(prior, current is null || flow == Flow.Waiting ? prior : current, node.InputChannel.Name);
        var before = comparison.Before;
        var items = comparison.After;
        var span = Math.Max(before.Length, items.Length);
        for (var index = 0; index < span; index++)
        {
            var known = comparison.KnownAt(index);
            var originId = prior is null ? logicalId : OriginalItemSource(prior, node.InputChannel.Name, index);
            var itemKey = $"{originId:N}:{index:D8}";
            if (!known && index < before.Length && index < items.Length && flow == Flow.Active && affected)
            {
                // Retire the old slot. A moved item must never acquire its predecessor's payment.
                AddItemEdges(itemKey, originId, Flow.Inactive, null, false, false);
                originId = Guid.Parse(current!.Id);
                itemKey = $"{originId:N}:{index:D8}";
            }
            else if (index >= before.Length && affected && current is not null)
            {
                originId = Guid.Parse(current.Id);
                itemKey = $"{originId:N}:{index:D8}";
            }
            var ambiguous = !known && index < items.Length && (index < before.Length || comparison.PreviouslyPresent(items[index]));
            AddItemEdges(itemKey, originId, index < items.Length ? flow : Flow.Inactive,
                index < items.Length ? items[index] : null, !known && affected, ambiguous);

            void AddItemEdges(string key, Guid sourceId, Flow state, JsonElement? item, bool newItem, bool uncertain)
            {
                var context = new ProcessStepIterationContext { ItemKey = key, ItemIndex = index,
                    SourceIteratorStepId = sourceId.ToString(), SourceIteratorBlueprintNodeId = node.Id.ToString() };
                JsonElement? payload = item.HasValue ? JsonSerializer.SerializeToElement(
                    new Dictionary<string, JsonElement> { ["item"] = item.Value }) : null;
                foreach (var binding in channel.OutboundBindings)
                    Observe(new(binding, current, context, state, affected, payload, true, false, newItem, uncertain));
            }
        }
    }

    private bool AcceptedWithoutExpansion(ProcessStepRecord? current)
    {
        while (current?.RecoveryAction == "Reuse" && current.ReusedFromStepId is { } previous)
            current = _byId.GetValueOrDefault(previous);
        return current?.Status is ProcessStepStatus.Failed or ProcessStepStatus.Skipped;
    }

    private Guid OriginalItemSource(ProcessStepRecord iterator, string channel, int index)
    {
        // Each expansion assigns identities relative to the previous effective expansion.
        // Reusing an older result still compares its items to the expansion it replaced.
        var current = iterator;
        while (true)
        {
            var previousId = current.RecoverySourceStepId;
            if (previousId is null || !_byId.TryGetValue(previousId, out var previous)) return Guid.Parse(current.Id);
            if (!CompareExpansion(previous, current, channel).KnownAt(index))
                return Guid.Parse(current.Id);
            current = previous;
        }
    }

    private ExpansionComparison CompareExpansion(ProcessStepRecord? previous, ProcessStepRecord? current, string channel)
    {
        var pair = (previous?.Id, current?.Id);
        if (_expansions.TryGetValue(pair, out var existing)) return existing;
        return _expansions[pair] = new(CollectionItems(previous, channel), CollectionItems(current, channel));
    }

    private sealed class ExpansionComparison(JsonElement[] before, JsonElement[] after)
    {
        public JsonElement[] Before => before;
        public JsonElement[] After => after;
        private readonly bool _unchanged = before.Length == after.Length && before.Zip(after).All(pair => JsonElement.DeepEquals(pair.First, pair.Second));
        private readonly ILookup<string, JsonElement> _previous = before.ToLookup(ScalarKey);
        private readonly ILookup<string, JsonElement> _current = after.ToLookup(ScalarKey);

        public bool KnownAt(int index)
        {
            if (index >= before.Length || index >= after.Length) return false;
            if (_unchanged) return true;
            var key = ScalarKey(after[index]);
            return JsonElement.DeepEquals(before[index], after[index]) && _previous[key].Count() == 1 && _current[key].Count() == 1;
        }

        public bool PreviouslyPresent(JsonElement item) => _previous.Contains(ScalarKey(item));

        // Collection Iterator items are typed scalars. Equivalent numeric spellings identify the same value.
        private static string ScalarKey(JsonElement item) => item.ValueKind switch
        {
            JsonValueKind.String => "s:" + item.GetString(),
            JsonValueKind.Number when item.TryGetDecimal(out var number) => "n:" + number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            _ => item.GetRawText()
        };
    }

    private JsonElement[] CollectionItems(ProcessStepRecord? step, string channel)
    {
        if (step is null) return [];
        if (_iteratorItems.TryGetValue(step.Id, out var cached)) return cached;
        return _iteratorItems[step.Id] = step.InputData?.TryGetValue(channel, out var value) == true && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToArray() : [];
    }

    private void Observe(BindingObservation edge)
    {
        if (!_inbound.TryGetValue(edge.Binding.DestinationBlueprintNodeId, out var edges))
            _inbound[edge.Binding.DestinationBlueprintNodeId] = edges = [];
        edges.Add(edge);
    }

    private InputHydrationPlan BuildHydration(ExecutableBlueprintNode node, ProcessStepRecord? prior,
        List<BindingObservation> edges, out List<string> missing)
    {
        var original = prior?.InputHydrationPlan;
        if (original is null && node.Id == blueprint.InitialNode.Id) original = process.Start?.InitialInputHydrationPlan;
        var result = new InputHydrationPlan
        {
            InputChannelName = node.InputChannel.Name,
            InitialInput = original?.InitialInput ?? LegacyInitialInput(node, prior),
            DesignTimeInputOverrides = original?.DesignTimeInputOverrides ?? node.DesignTimeInputOverrides,
            RuntimeInputOverrides = original?.RuntimeInputOverrides ?? process.NodeOverrides.GetValueOrDefault(node.Id.ToString()) ?? []
        };
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        BlueprintInputMaterializer.ApplyInputPresence(available, result.InitialInput);
        foreach (var edge in edges.Where(edge => edge.State == Flow.Active && edge.Payload.HasValue).OrderBy(e => e.Binding.Id))
        {
            var values = BlueprintInputMaterializer.MaterializePartialInput(edge.Payload!.Value, edge.Binding.PropertyMappings);
            BlueprintInputMaterializer.ApplyInputPresence(available, values);
            result.Sources.Add(new InputHydrationSource
            {
                BindingId = edge.Binding.Id, SourceProcessStepId = edge.Source!.Id,
                SourceOutputChannelName = edge.Binding.SourceChannelName,
                PropertyMappings = edge.Binding.PropertyMappings.Where(m => values.ContainsKey(m.TargetProperty)).Select(m =>
                    new InputHydrationPropertyMapping { SourceProperty = m.SourceProperty, TargetProperty = m.TargetProperty }).ToList(),
                InlineInput = edge.Inline ? values : null
            });
        }
        BlueprintInputMaterializer.ApplyInputPresence(available, result.DesignTimeInputOverrides);
        BlueprintInputMaterializer.ApplyInputPresence(available, result.RuntimeInputOverrides);
        missing = node.InputChannel.RequiredProperties.Where(key => !available.Contains(key)).ToList();
        return result;
    }

    private Dictionary<string, JsonElement> LegacyInitialInput(ExecutableBlueprintNode node, ProcessStepRecord? prior)
    {
        if (node.Id == blueprint.InitialNode.Id && prior?.InputData?.TryGetValue(node.InputChannel.Name, out var input) == true
            && input.ValueKind == JsonValueKind.Object)
            return input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        return [];
    }

    private IEnumerable<ExecutableBlueprintNode> OrderedNodes()
    {
        var remaining = blueprint.AllNodes.ToDictionary(n => n.Id, n => n.InboundBindings.Count);
        var queue = new Queue<ExecutableBlueprintNode>(blueprint.AllNodes.Where(n => remaining[n.Id] == 0));
        var visited = 0;
        while (queue.TryDequeue(out var node))
        {
            yield return node;
            visited++;
            foreach (var binding in node.OutputChannels.Values.SelectMany(channel => channel.OutboundBindings))
                if (--remaining[binding.DestinationBlueprintNodeId] == 0)
                    queue.Enqueue(blueprint.NodesById[binding.DestinationBlueprintNodeId]);
        }
        if (visited != remaining.Count) throw new InvalidOperationException("Recovery requires an acyclic pinned Blueprint.");
    }

    private enum Flow { Active, Bypass, Inactive, Waiting }
    private sealed record BindingObservation(ExecutableBlueprintBinding Binding, ProcessStepRecord? Source,
        ProcessStepIterationContext? Iteration, Flow State, bool Affected, JsonElement? Payload,
        bool Inline, bool NewPath, bool NewItem, bool AmbiguousItem);
}
