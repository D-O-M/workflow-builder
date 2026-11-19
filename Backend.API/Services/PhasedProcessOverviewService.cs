using System.Text.Json;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Interfaces.Repositories;
using Backend.Models.ViewModels;
using Backend.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Backend.API.Services;

public sealed class PhasedProcessOverviewService(IPhasedProcessRepository parents, BlueprintDbContext db)
{
    public async Task<PhasedProcessOverviewViewModel> GetOverviewAsync(
        int definitionId, Guid parentId, CancellationToken cancellationToken)
    {
        var parent = await parents.GetOverviewAsync(parentId, definitionId, cancellationToken);
        var version = await db.PhasedBlueprintVersions.AsNoTracking()
            .Where(v => v.Id == parent.PhasedBlueprintVersionId && v.PhasedBlueprintDefinitionId == definitionId)
            .Select(v => new
            {
                v.Id,
                v.Version,
                v.PhasedBlueprintDefinition.DisplayName,
                Phases = v.Phases.Select(p => new { p.Id, p.BlueprintDefinition.DisplayName, p.VersionPolicy }).ToList(),
                Transitions = v.Transitions.OrderBy(t => t.SourcePhase.PhaseIndex)
                    .Select(t => new PhasedProcessTransitionViewModel(t.Id, t.SourcePhaseId, t.DestinationPhaseId)).ToList()
            })
            .AsSplitQuery()
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new EntityNotFoundException<PhasedBlueprintVersion>(parent.PhasedBlueprintVersionId);

        var recordedVersionIds = parent.Phases.Select(p => p.BlueprintVersionId).OfType<int>().Distinct().ToArray();
        var versionNumbers = await db.BlueprintVersions.AsNoTracking()
            .Where(v => recordedVersionIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => (int?)v.Version, cancellationToken);
        var definitions = version.Phases.ToDictionary(p => p.Id);
        var phases = parent.Phases.OrderBy(p => p.PhaseIndex).Select(p =>
        {
            var phaseId = Guid.Parse(p.PhaseId);
            var definition = definitions[phaseId];
            return new PhasedProcessPhaseOverviewViewModel(
                phaseId, p.PhaseIndex, p.PhaseName, p.BlueprintDefinitionId, definition.DisplayName,
                p.BlueprintVersionId, p.BlueprintVersionId is int id ? versionNumbers.GetValueOrDefault(id) : null,
                definition.VersionPolicy.ToString(), Guid.TryParse(p.ChildProcessId, out var childId) ? childId : null,
                p.Status, p.StartedAt, p.CompletedAt, p.FailureMessage);
        }).ToList();
        return new PhasedProcessOverviewViewModel(
            parentId, definitionId, version.DisplayName, version.Id, version.Version, parent.Status,
            parent.CreatedAt, parent.UpdatedAt, Guid.TryParse(parent.CurrentPhaseId, out var current) ? current : null,
            phases, version.Transitions);
    }

    public async Task<PhasedProcessHandoffViewModel> GetHandoffAsync(
        int definitionId, Guid parentId, Guid sourcePhaseId, CancellationToken cancellationToken)
    {
        var parent = await parents.GetAsync(parentId, definitionId, cancellationToken);
        var transition = await db.PhasedBlueprintTransitions.AsNoTracking()
            .Include(t => t.HandoffMappings)
            .SingleOrDefaultAsync(t => t.PhasedBlueprintVersionId == parent.PhasedBlueprintVersionId &&
                t.PhasedBlueprintVersion.PhasedBlueprintDefinitionId == definitionId && t.SourcePhaseId == sourcePhaseId,
                cancellationToken)
            ?? throw new EntityNotFoundException<PhasedBlueprintTransition>(sourcePhaseId.ToString());
        var nodeIds = transition.HandoffMappings.Select(m => m.SourceBlueprintNodeId).OfType<Guid>()
            .Concat(transition.HandoffMappings.Select(m => m.TargetBlueprintNodeId)).Distinct().ToArray();
        var nodeNames = await db.BlueprintNodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id))
            .Select(n => new
            {
                n.Id,
                Name = n.NodeKind == BlueprintNodeKind.ConditionalBranch ? "Conditional Branch"
                    : n.NodeKind == BlueprintNodeKind.CollectionIterator ? "Collection Iterator"
                    : n.TaskDefinition == null ? null : n.TaskDefinition.DisplayName ?? n.TaskDefinition.Name
            })
            .ToDictionaryAsync(n => n.Id, n => n.Name, cancellationToken);
        parent.PhaseResults.TryGetValue(sourcePhaseId.ToString(), out var result);
        result ??= [];

        var mappings = new List<PhasedProcessHandoffMappingViewModel>();
        var representedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in transition.HandoffMappings.OrderBy(m => m.Id))
        {
            var key = PhasedProcessFinalizationService.BuildHandoffResultKey(mapping);
            var hasValue = result.TryGetValue(key, out var value);
            representedKeys.Add(key);
            // An alias can be shared by multiple target nodes. Only suppress it if its value is accounted for.
            if (hasValue && result.TryGetValue(mapping.TargetProperty, out var alias) && JsonElement.DeepEquals(value, alias))
            {
                representedKeys.Add(mapping.TargetProperty);
            }

            mappings.Add(new PhasedProcessHandoffMappingViewModel(
                mapping.SourceBlueprintNodeId,
                mapping.SourceBlueprintNodeId is Guid sourceId ? nodeNames.GetValueOrDefault(sourceId) : null,
                mapping.SourceChannelName, mapping.SourceProperty, mapping.TargetBlueprintNodeId,
                nodeNames.GetValueOrDefault(mapping.TargetBlueprintNodeId), mapping.TargetProperty,
                hasValue, hasValue ? value.GetRawText() : null));
        }

        var unattributed = result.Where(entry => !representedKeys.Contains(entry.Key))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new PhasedProcessUnattributedValueViewModel(entry.Key, entry.Value.GetRawText())).ToList();
        return new PhasedProcessHandoffViewModel(
            transition.Id, transition.SourcePhaseId, transition.DestinationPhaseId, mappings, unattributed);
    }
}
