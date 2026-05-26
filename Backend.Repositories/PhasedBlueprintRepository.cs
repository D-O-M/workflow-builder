using System.Text.RegularExpressions;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.Versioning;
using Backend.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Backend.Repositories;

public sealed class PhasedBlueprintRepository : IPhasedBlueprintRepository
{
    private readonly BlueprintDbContext _context;

    public PhasedBlueprintRepository(BlueprintDbContext context)
    {
        _context = context;
    }

    public async Task<List<PhasedBlueprintViewModel>> GetAllAsync(CancellationToken cancellationToken)
    {
        var definitions = await _context.PhasedBlueprintDefinitions
            .Include(definition => definition.Versions)
                .ThenInclude(version => version.Phases)
                    .ThenInclude(phase => phase.BlueprintDefinition)
            .Include(definition => definition.Versions)
                .ThenInclude(version => version.Phases)
                    .ThenInclude(phase => phase.BlueprintVersion)
            .Include(definition => definition.Versions)
                .ThenInclude(version => version.Transitions)
                    .ThenInclude(transition => transition.HandoffMappings)
            .Include(definition => definition.Versions)
                .ThenInclude(version => version.Transitions)
                    .ThenInclude(transition => transition.CorrelationMappings)
            .AsNoTracking()
            .AsSplitQuery()
            .OrderBy(definition => definition.Id)
            .ToListAsync(cancellationToken);

        return definitions.Select(MapDefinition).ToList();
    }

    public async Task<PhasedBlueprintViewModel> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        var definition = await _context.PhasedBlueprintDefinitions
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.Phases)
                    .ThenInclude(phase => phase.BlueprintDefinition)
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.Phases)
                    .ThenInclude(phase => phase.BlueprintVersion)
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.Transitions)
                    .ThenInclude(transition => transition.HandoffMappings)
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.Transitions)
                    .ThenInclude(transition => transition.CorrelationMappings)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        return definition is null
            ? throw new EntityNotFoundException<PhasedBlueprintViewModel>(id)
            : MapDefinition(definition);
    }

    public async Task<PhasedBlueprintViewModel> GetByKeyAsync(string key, CancellationToken cancellationToken)
    {
        var normalizedKey = key.Trim();
        var definition = await _context.PhasedBlueprintDefinitions
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.Phases)
                    .ThenInclude(phase => phase.BlueprintDefinition)
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.Phases)
                    .ThenInclude(phase => phase.BlueprintVersion)
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.Transitions)
                    .ThenInclude(transition => transition.HandoffMappings)
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.Transitions)
                    .ThenInclude(transition => transition.CorrelationMappings)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Key == normalizedKey, cancellationToken);

        return definition is null
            ? throw new EntityNotFoundException<PhasedBlueprintViewModel>(normalizedKey)
            : MapDefinition(definition);
    }

    public async Task<PhasedBlueprintVersion> GetVersionGraphAsync(
        int phasedBlueprintVersionId,
        CancellationToken cancellationToken)
    {
        var version = await _context.PhasedBlueprintVersions
            .Include(candidate => candidate.PhasedBlueprintDefinition)
            .Include(candidate => candidate.Phases)
                .ThenInclude(phase => phase.BlueprintDefinition)
            .Include(candidate => candidate.Phases)
                .ThenInclude(phase => phase.BlueprintVersion)
            .Include(candidate => candidate.Transitions)
                .ThenInclude(transition => transition.HandoffMappings)
            .Include(candidate => candidate.Transitions)
                .ThenInclude(transition => transition.CorrelationMappings)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Id == phasedBlueprintVersionId, cancellationToken);

        return version is null
            ? throw new EntityNotFoundException<PhasedBlueprintVersionViewModel>(phasedBlueprintVersionId)
            : version;
    }

    public async Task<int> GetLatestVersionIdAsync(
        int phasedBlueprintDefinitionId,
        CancellationToken cancellationToken)
    {
        var versionId = await _context.PhasedBlueprintVersions
            .AsNoTracking()
            .Where(version => version.PhasedBlueprintDefinitionId == phasedBlueprintDefinitionId)
            .OrderByDescending(version => version.Version)
            .Select(version => (int?)version.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return versionId
            ?? throw new EntityNotFoundException<PhasedBlueprintViewModel>(phasedBlueprintDefinitionId);
    }

    public async Task<PhasedBlueprintViewModel> CreateAsync(
        PhasedBlueprintInputModel model,
        CancellationToken cancellationToken)
    {
        var displayName = model.Name.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidDataException("Phased blueprint name is required.");
        }

        await ValidateVersionPayloadAsync(model.Phases, model.Transitions, cancellationToken);

        var definition = new PhasedBlueprintDefinition
        {
            Key = string.IsNullOrWhiteSpace(model.Key)
                ? await GenerateUniqueKeyAsync(displayName, cancellationToken)
                : model.Key.Trim(),
            DisplayName = displayName,
        };

        if (await _context.PhasedBlueprintDefinitions.AnyAsync(candidate => candidate.Key == definition.Key, cancellationToken))
        {
            throw new InvalidDataException($"Phased blueprint key '{definition.Key}' already exists.");
        }

        var version = CreateVersionEntity(definition, 1, model.Description, model.Phases, model.Transitions);
        definition.Versions.Add(version);
        _context.PhasedBlueprintDefinitions.Add(definition);
        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(definition.Id, cancellationToken);
    }

    public async Task<PhasedBlueprintVersionViewModel> CreateVersionAsync(
        PhasedBlueprintVersionInputModel model,
        CancellationToken cancellationToken)
    {
        var definition = await _context.PhasedBlueprintDefinitions
            .Include(candidate => candidate.Versions)
            .FirstOrDefaultAsync(candidate => candidate.Id == model.PhasedBlueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<PhasedBlueprintViewModel>(model.PhasedBlueprintDefinitionId);
        }

        await ValidateVersionPayloadAsync(model.Phases, model.Transitions, cancellationToken);

        var nextVersion = definition.Versions.Count == 0
            ? 1
            : definition.Versions.Max(version => version.Version) + 1;
        var version = CreateVersionEntity(definition, nextVersion, model.Description, model.Phases, model.Transitions);
        definition.Versions.Add(version);
        await _context.SaveChangesAsync(cancellationToken);

        return MapVersion(await GetVersionGraphAsync(version.Id, cancellationToken));
    }

    private PhasedBlueprintVersion CreateVersionEntity(
        PhasedBlueprintDefinition definition,
        int versionNumber,
        string? description,
        IReadOnlyCollection<PhasedBlueprintPhaseInputModel> phaseModels,
        IReadOnlyCollection<PhasedBlueprintTransitionInputModel> transitionModels)
    {
        var version = new PhasedBlueprintVersion
        {
            PhasedBlueprintDefinition = definition,
            Version = versionNumber,
            Description = description,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var phasesById = new Dictionary<Guid, PhasedBlueprintPhase>();
        foreach (var phaseModel in phaseModels.OrderBy(phase => phase.PhaseIndex))
        {
            var phaseId = phaseModel.PhaseId == Guid.Empty ? Guid.NewGuid() : phaseModel.PhaseId;
            var phase = new PhasedBlueprintPhase
            {
                Id = phaseId,
                PhasedBlueprintVersion = version,
                PhaseIndex = phaseModel.PhaseIndex,
                Name = phaseModel.Name.Trim(),
                BlueprintDefinitionId = phaseModel.BlueprintDefinitionId,
                BlueprintVersionId = ParseVersionPolicy(phaseModel.VersionPolicy) == PhasedBlueprintPhaseVersionPolicy.Pinned
                    ? phaseModel.BlueprintVersionId
                    : null,
                VersionPolicy = ParseVersionPolicy(phaseModel.VersionPolicy),
            };
            phasesById[phaseId] = phase;
            version.Phases.Add(phase);
        }

        foreach (var transitionModel in transitionModels)
        {
            var transition = new PhasedBlueprintTransition
            {
                Id = transitionModel.TransitionId == Guid.Empty ? Guid.NewGuid() : transitionModel.TransitionId,
                PhasedBlueprintVersion = version,
                SourcePhaseId = transitionModel.SourcePhaseId,
                DestinationPhaseId = transitionModel.DestinationPhaseId,
                HandoffMappings = transitionModel.HandoffMappings.Select(mapping => new PhasedBlueprintHandoffMapping
                {
                    SourceKind = ParseHandoffSourceKind(mapping.SourceKind),
                    SourceBlueprintNodeId = mapping.SourceBlueprintNodeId,
                    SourceChannelName = string.IsNullOrWhiteSpace(mapping.SourceChannelName) ? null : mapping.SourceChannelName.Trim(),
                    SourceProperty = mapping.SourceProperty.Trim(),
                    BridgePosition = NormalizeBridgePosition(mapping.BridgePosition),
                    TargetBlueprintNodeId = mapping.TargetBlueprintNodeId,
                    TargetProperty = mapping.TargetProperty.Trim(),
                }).ToList(),
                CorrelationMappings = transitionModel.CorrelationMappings.Select(mapping => new PhasedBlueprintCorrelationMapping
                {
                    SourceResultProperty = mapping.SourceResultProperty.Trim(),
                    TriggerProperty = mapping.TriggerProperty.Trim(),
                }).ToList(),
            };

            version.Transitions.Add(transition);
        }

        return version;
    }

    private async Task ValidateVersionPayloadAsync(
        IReadOnlyCollection<PhasedBlueprintPhaseInputModel> phases,
        IReadOnlyCollection<PhasedBlueprintTransitionInputModel> transitions,
        CancellationToken cancellationToken)
    {
        if (phases.Count is < 1 or > 4)
        {
            throw new InvalidDataException("Phased blueprints must contain between one and four phases.");
        }

        var phaseIds = new HashSet<Guid>();
        foreach (var phase in phases)
        {
            if (phases.Count > 1 && phase.PhaseId == Guid.Empty)
            {
                throw new InvalidDataException("Multi-phase versions must include stable phase IDs so transitions can reference them.");
            }

            var phaseId = phase.PhaseId == Guid.Empty ? Guid.NewGuid() : phase.PhaseId;
            if (!phaseIds.Add(phaseId))
            {
                throw new InvalidDataException("Phase IDs must be unique.");
            }

            if (string.IsNullOrWhiteSpace(phase.Name))
            {
                throw new InvalidDataException("Phase name is required.");
            }

            var policy = ParseVersionPolicy(phase.VersionPolicy);
            if (policy == PhasedBlueprintPhaseVersionPolicy.Pinned && phase.BlueprintVersionId is null)
            {
                throw new InvalidDataException("Pinned phases must reference a blueprint version.");
            }
        }

        var orderedPhases = phases.OrderBy(phase => phase.PhaseIndex).ToList();
        if (!orderedPhases.Select(phase => phase.PhaseIndex).SequenceEqual(Enumerable.Range(0, orderedPhases.Count)))
        {
            throw new InvalidDataException("Phase indexes must be contiguous from zero.");
        }

        await ValidateReferencedBlueprintsAsync(phases, cancellationToken);

        if (transitions.Count != Math.Max(0, phases.Count - 1))
        {
            throw new InvalidDataException("Phased blueprints must have one adjacent transition between each pair of phases.");
        }

        var phasesById = phases.ToDictionary(phase => phase.PhaseId);
        foreach (var transition in transitions)
        {
            if (!phasesById.TryGetValue(transition.SourcePhaseId, out var sourcePhase) ||
                !phasesById.TryGetValue(transition.DestinationPhaseId, out var destinationPhase))
            {
                throw new InvalidDataException("Transitions must reference phases in the same phased blueprint version.");
            }

            if (destinationPhase.PhaseIndex != sourcePhase.PhaseIndex + 1)
            {
                throw new InvalidDataException("Only adjacent phase transitions are supported.");
            }

            await ValidateTransitionMappingsAsync(sourcePhase, destinationPhase, transition, cancellationToken);
        }
    }

    private async Task ValidateReferencedBlueprintsAsync(
        IReadOnlyCollection<PhasedBlueprintPhaseInputModel> phases,
        CancellationToken cancellationToken)
    {
        var definitionIds = phases.Select(phase => phase.BlueprintDefinitionId).Distinct().ToList();
        var existingDefinitionIds = await _context.BlueprintDefinitions
            .AsNoTracking()
            .Where(definition => definitionIds.Contains(definition.Id))
            .Select(definition => definition.Id)
            .ToListAsync(cancellationToken);

        if (existingDefinitionIds.Count != definitionIds.Count)
        {
            throw new InvalidDataException("Every phase must reference an existing blueprint definition.");
        }

        var pinnedVersionIds = phases
            .Where(phase => ParseVersionPolicy(phase.VersionPolicy) == PhasedBlueprintPhaseVersionPolicy.Pinned)
            .Select(phase => phase.BlueprintVersionId!.Value)
            .Distinct()
            .ToList();

        if (pinnedVersionIds.Count == 0)
        {
            return;
        }

        var versions = await _context.BlueprintVersions
            .AsNoTracking()
            .Where(version => pinnedVersionIds.Contains(version.Id))
            .Select(version => new { version.Id, version.BlueprintDefinitionId })
            .ToListAsync(cancellationToken);

        if (versions.Count != pinnedVersionIds.Count)
        {
            throw new InvalidDataException("Pinned phases must reference existing blueprint versions.");
        }

        var versionDefinitionById = versions.ToDictionary(version => version.Id, version => version.BlueprintDefinitionId);
        foreach (var phase in phases.Where(phase => phase.BlueprintVersionId.HasValue))
        {
            if (versionDefinitionById.TryGetValue(phase.BlueprintVersionId!.Value, out var definitionId) &&
                definitionId != phase.BlueprintDefinitionId)
            {
                throw new InvalidDataException("Pinned phase blueprint versions must belong to the phase blueprint definition.");
            }
        }
    }

    private async Task ValidateTransitionMappingsAsync(
        PhasedBlueprintPhaseInputModel sourcePhase,
        PhasedBlueprintPhaseInputModel destinationPhase,
        PhasedBlueprintTransitionInputModel transition,
        CancellationToken cancellationToken)
    {
        var sourceBlueprintVersionId = await ResolveValidationBlueprintVersionIdAsync(sourcePhase, cancellationToken);
        var destinationBlueprintVersionId = await ResolveValidationBlueprintVersionIdAsync(destinationPhase, cancellationToken);
        var sourceNodes = await LoadBlueprintNodesAsync(sourceBlueprintVersionId, cancellationToken);
        var destinationNodes = await LoadBlueprintNodesAsync(destinationBlueprintVersionId, cancellationToken);

        var targetProperties = new HashSet<string>(StringComparer.Ordinal);
        var targetResultProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in transition.HandoffMappings)
        {
            if (mapping.TargetBlueprintNodeId == Guid.Empty)
            {
                throw new InvalidDataException("Phase handoff mappings must reference a destination node.");
            }

            if (!destinationNodes.TryGetValue(mapping.TargetBlueprintNodeId, out var destinationNode))
            {
                throw new InvalidDataException("Phase handoff mappings must reference a destination node in the destination phase.");
            }

            var destinationInputProperties = GetInputProperties(destinationNode);
            var targetKey = $"{mapping.TargetBlueprintNodeId:N}:{mapping.TargetProperty}";
            if (!targetProperties.Add(targetKey))
            {
                throw new InvalidDataException("A phase handoff target node property can only be mapped once.");
            }

            if (!destinationInputProperties.ContainsKey(mapping.TargetProperty))
            {
                throw new InvalidDataException($"Phase handoff target property '{mapping.TargetProperty}' does not exist on destination node '{mapping.TargetBlueprintNodeId}'.");
            }

            targetResultProperties.Add(mapping.TargetProperty);
            var sourceKind = ParseHandoffSourceKind(mapping.SourceKind);
            if (mapping.SourceBlueprintNodeId is not Guid sourceNodeId ||
                !sourceNodes.TryGetValue(sourceNodeId, out var sourceNode))
            {
                throw new InvalidDataException("Output handoff mappings must reference a source node in the source phase.");
            }

            if (string.IsNullOrWhiteSpace(mapping.SourceChannelName))
            {
                throw new InvalidDataException("Output handoff mappings must reference a source channel.");
            }

            var sourceProperties = GetOutputProperties(sourceNode, mapping.SourceChannelName);
            if (!sourceProperties.TryGetValue(mapping.SourceProperty, out var sourceKindName))
            {
                throw new InvalidDataException($"Phase handoff source property '{mapping.SourceProperty}' does not exist.");
            }

            if (destinationInputProperties.TryGetValue(mapping.TargetProperty, out var targetKindName) &&
                !string.Equals(sourceKindName, targetKindName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Phase handoff source property '{mapping.SourceProperty}' ({sourceKindName}) cannot map to destination property '{mapping.TargetProperty}' ({targetKindName}) on node '{mapping.TargetBlueprintNodeId}'.");
            }
        }

        var triggerProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var correlation in transition.CorrelationMappings)
        {
            if (string.IsNullOrWhiteSpace(correlation.SourceResultProperty))
            {
                throw new InvalidDataException("Correlation mappings must include a source result property.");
            }

            if (string.IsNullOrWhiteSpace(correlation.TriggerProperty))
            {
                throw new InvalidDataException("Correlation mappings must include a trigger property.");
            }

            if (!targetResultProperties.Contains(correlation.SourceResultProperty))
            {
                throw new InvalidDataException("Correlation mappings must reference a handoff result property.");
            }

            if (!triggerProperties.Add(correlation.TriggerProperty))
            {
                throw new InvalidDataException("Correlation trigger property names must be unique within a transition.");
            }
        }
    }

    private async Task<int> ResolveValidationBlueprintVersionIdAsync(
        PhasedBlueprintPhaseInputModel phase,
        CancellationToken cancellationToken)
    {
        if (ParseVersionPolicy(phase.VersionPolicy) == PhasedBlueprintPhaseVersionPolicy.Pinned)
        {
            return phase.BlueprintVersionId!.Value;
        }

        var latest = await _context.BlueprintVersions
            .AsNoTracking()
            .Where(version => version.BlueprintDefinitionId == phase.BlueprintDefinitionId)
            .OrderByDescending(version => version.Version)
            .Select(version => (int?)version.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return latest ?? throw new InvalidDataException("Latest phase policy requires an existing blueprint version.");
    }

    private async Task<Dictionary<Guid, BlueprintNode>> LoadBlueprintNodesAsync(
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var nodes = await _context.BlueprintNodes
            .AsNoTracking()
            .Where(node => node.BlueprintVersionId == blueprintVersionId)
            .Include(node => node.TaskDefinition!)
                .ThenInclude(taskDefinition => taskDefinition.Versions)
                    .ThenInclude(taskVersion => taskVersion.Channels)
                        .ThenInclude(channel => channel.Properties)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return nodes.ToDictionary(node => node.Id);
    }

    private async Task<BlueprintNode> LoadInitialBlueprintNodeAsync(
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var version = await _context.BlueprintVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == blueprintVersionId, cancellationToken)
            ?? throw new InvalidDataException("Destination phase blueprint version does not exist.");
        if (version.StartingBlueprintNodeId is not Guid startingBlueprintNodeId)
        {
            throw new InvalidDataException("Destination phase blueprint version does not have a starting node.");
        }

        return await _context.BlueprintNodes
            .AsNoTracking()
            .Where(node => node.Id == startingBlueprintNodeId)
            .Include(node => node.TaskDefinition!)
                .ThenInclude(taskDefinition => taskDefinition.Versions)
                    .ThenInclude(taskVersion => taskVersion.Channels)
                        .ThenInclude(channel => channel.Properties)
            .AsSplitQuery()
            .FirstAsync(cancellationToken);
    }

    private static Dictionary<string, string> GetInputProperties(BlueprintNode node)
    {
        if (node.NodeKind == BlueprintNodeKind.CollectionIterator)
        {
            var itemKind = NormalizeCollectionIteratorItemKind(node.CollectionIteratorItemKind);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["items"] = WorkflowPropertyTypeName.ToCollectionKindName(itemKind),
            };
        }

        var version = ResolveConcreteVersion(node);
        return version.InputChannels.Single().Properties.ToDictionary(
            property => property.Name,
            property => WorkflowPropertyTypeName.Format(property.Kind, property.IsCollection),
            StringComparer.Ordinal);
    }

    private static Dictionary<string, string> GetOutputProperties(BlueprintNode node, string channelName)
    {
        if (node.NodeKind == BlueprintNodeKind.CollectionIterator)
        {
            var itemKind = NormalizeCollectionIteratorItemKind(node.CollectionIteratorItemKind);
            return string.Equals(channelName, "Item", StringComparison.Ordinal)
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["item"] = itemKind }
                : [];
        }

        var version = ResolveConcreteVersion(node);
        return version.OutputChannels
            .FirstOrDefault(channel => string.Equals(channel.Name, channelName, StringComparison.Ordinal))
            ?.Properties
            .ToDictionary(
                property => property.Name,
                property => WorkflowPropertyTypeName.Format(property.Kind, property.IsCollection),
                StringComparer.Ordinal)
            ?? [];
    }

    private static TaskVersion ResolveConcreteVersion(BlueprintNode node)
    {
        if (node.TaskDefinition is null)
        {
            throw new InvalidDataException("Plugin task nodes must have a task definition.");
        }

        return node.TaskDefinition.Versions
            .Where(version => SemanticVersionUtility.IsSameLane(version.Version, node.AllowedMajor, node.AllowedMinor))
            .OrderByDescending(version => version.Version)
            .First();
    }

    private static string NormalizeCollectionIteratorItemKind(string? itemKind) =>
        !string.IsNullOrWhiteSpace(itemKind) &&
        WorkflowPropertyTypeName.TryParse(itemKind, out var parsedKind, out var isCollection) &&
        !isCollection
            ? WorkflowPropertyTypeName.Format(parsedKind)
            : WorkflowPropertyKind.String.ToString();

    private async Task<string> GenerateUniqueKeyAsync(string displayName, CancellationToken cancellationToken)
    {
        var baseKey = Slugify(displayName);
        if (string.IsNullOrWhiteSpace(baseKey))
        {
            baseKey = "phased-blueprint";
        }

        var existingKeys = await _context.PhasedBlueprintDefinitions
            .AsNoTracking()
            .Where(definition => definition.Key == baseKey || definition.Key.StartsWith(baseKey + "-"))
            .Select(definition => definition.Key)
            .ToListAsync(cancellationToken);

        if (!existingKeys.Contains(baseKey, StringComparer.Ordinal))
        {
            return baseKey;
        }

        var suffix = 2;
        while (existingKeys.Contains($"{baseKey}-{suffix}", StringComparer.Ordinal))
        {
            suffix++;
        }

        return $"{baseKey}-{suffix}";
    }

    private static string Slugify(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
        return normalized.Trim('-');
    }

    private static PhasedBlueprintPhaseVersionPolicy ParseVersionPolicy(string? value) =>
        Enum.TryParse<PhasedBlueprintPhaseVersionPolicy>(value, ignoreCase: true, out var parsed)
            ? parsed
            : PhasedBlueprintPhaseVersionPolicy.Pinned;

    private static PhasedBlueprintHandoffSourceKind ParseHandoffSourceKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PhasedBlueprintHandoffSourceKind.Output;
        }

        return Enum.TryParse<PhasedBlueprintHandoffSourceKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Phase handoff source kind '{value}' is not supported.");
    }

    private static double NormalizeBridgePosition(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, 0d, 1d)
            : 0.5d;

    private static PhasedBlueprintViewModel MapDefinition(PhasedBlueprintDefinition definition) =>
        new()
        {
            Id = definition.Id,
            Key = definition.Key,
            Name = definition.DisplayName,
            Description = string.Empty,
            Versions = definition.Versions
                .OrderByDescending(version => version.Version)
                .Select(MapVersion)
                .ToList()
        };

    private static PhasedBlueprintVersionViewModel MapVersion(PhasedBlueprintVersion version) =>
        new()
        {
            PhasedBlueprintVersionId = version.Id,
            PhasedBlueprintDefinitionId = version.PhasedBlueprintDefinitionId,
            Version = version.Version,
            Description = version.Description,
            CreatedAt = version.CreatedAt,
            Phases = version.Phases
                .OrderBy(phase => phase.PhaseIndex)
                .Select(phase => new PhasedBlueprintPhaseViewModel
                {
                    PhaseId = phase.Id,
                    PhaseIndex = phase.PhaseIndex,
                    Name = phase.Name,
                    BlueprintDefinitionId = phase.BlueprintDefinitionId,
                    BlueprintName = phase.BlueprintDefinition.DisplayName,
                    BlueprintVersionId = phase.BlueprintVersionId,
                    BlueprintVersionNumber = phase.BlueprintVersion?.Version,
                    VersionPolicy = phase.VersionPolicy.ToString()
                })
                .ToList(),
            Transitions = version.Transitions
                .OrderBy(transition => version.Phases.FirstOrDefault(phase => phase.Id == transition.SourcePhaseId)?.PhaseIndex ?? 0)
                .Select(transition => new PhasedBlueprintTransitionViewModel
                {
                    TransitionId = transition.Id,
                    SourcePhaseId = transition.SourcePhaseId,
                    DestinationPhaseId = transition.DestinationPhaseId,
                    HandoffMappings = transition.HandoffMappings.Select(mapping => new PhasedBlueprintHandoffMappingViewModel
                    {
                        SourceKind = mapping.SourceKind.ToString(),
                        SourceBlueprintNodeId = mapping.SourceBlueprintNodeId,
                        SourceChannelName = mapping.SourceChannelName,
                        SourceProperty = mapping.SourceProperty,
                        BridgePosition = NormalizeBridgePosition(mapping.BridgePosition),
                        TargetBlueprintNodeId = mapping.TargetBlueprintNodeId,
                        TargetProperty = mapping.TargetProperty
                    }).ToList(),
                    CorrelationMappings = transition.CorrelationMappings.Select(mapping => new PhasedBlueprintCorrelationMappingViewModel
                    {
                        SourceResultProperty = mapping.SourceResultProperty,
                        TriggerProperty = mapping.TriggerProperty
                    }).ToList()
                })
                .ToList()
        };
}
