using System.Text.Json;
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

public class BlueprintRepository : IBlueprintRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly BlueprintDbContext _context;

    public BlueprintRepository(BlueprintDbContext context)
    {
        _context = context;
    }

    public async Task<ExecutableBlueprint> GetFullBlueprintById(
        int id,
        CancellationToken cancellationToken)
    {
        var blueprintVersionEntity = await _context.BlueprintVersions
            .AsNoTracking()
            .Include(version => version.BlueprintDefinition)
            .FirstOrDefaultAsync(version => version.Id == id, cancellationToken);

        if (blueprintVersionEntity is null)
        {
            throw new EntityNotFoundException<ExecutableBlueprint>(id);
        }

        var nodeEntities = await _context.BlueprintNodes
            .Where(node => node.BlueprintVersionId == id)
            .Include(node => node.TaskDefinition)
                .ThenInclude(taskDefinition => taskDefinition.Versions)
                    .ThenInclude(taskVersion => taskVersion.Channels)
                        .ThenInclude(channel => channel.Properties)
            .Include(node => node.OutgoingConnections)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        blueprintVersionEntity.BlueprintNodes = nodeEntities;

        var nodesById = new Dictionary<Guid, ExecutableBlueprintNode>(nodeEntities.Count);
        foreach (var nodeEntity in nodeEntities)
        {
            var taskVersion = ResolveConcreteVersion(nodeEntity, requireActive: true);
            var inputChannelEntity = taskVersion.InputChannels.Single();

            var inputChannel = new ExecutableBlueprintChannel
            {
                Id = inputChannelEntity.Id,
                Name = inputChannelEntity.Name,
                Properties = inputChannelEntity.Properties.ToDictionary(
                    property => property.Name,
                    property => property.Kind.ToString(),
                    StringComparer.Ordinal),
                OutboundBindings = []
            };

            var outputChannels = taskVersion.OutputChannels.ToDictionary(
                channel => channel.Name,
                channel => new ExecutableBlueprintChannel
                {
                    Id = channel.Id,
                    Name = channel.Name,
                    Properties = channel.Properties.ToDictionary(
                        property => property.Name,
                        property => property.Kind.ToString(),
                        StringComparer.Ordinal),
                    OutboundBindings = []
                },
                StringComparer.Ordinal);

            nodesById[nodeEntity.Id] = new ExecutableBlueprintNode
            {
                Id = nodeEntity.Id,
                Name = taskVersion.TaskDefinition!.Name,
                TaskDefinitionId = nodeEntity.TaskDefinitionId,
                AllowedMajor = nodeEntity.AllowedMajor,
                AllowedMinor = nodeEntity.AllowedMinor,
                ResolvedVersion = taskVersion.Version,
                ResolvedTaskVersionId = taskVersion.Id,
                ExecutionOrder = 0,
                InputChannel = inputChannel,
                OutputChannels = outputChannels,
                InboundBindings = [],
                DesignTimeInputOverrides = DeserializeJsonMap(nodeEntity.DesignTimeInputOverridesJson)
            };
        }

        var executionOrderByNodeId = ComputeExecutionOrder(blueprintVersionEntity, nodeEntities);
        foreach (var (nodeId, order) in executionOrderByNodeId)
        {
            nodesById[nodeId].ExecutionOrder = order;
        }

        foreach (var nodeEntity in nodeEntities)
        {
            var sourceNode = nodesById[nodeEntity.Id];

            foreach (var connection in nodeEntity.OutgoingConnections)
            {
                if (connection.DestinationBlueprintNodeId is not Guid destinationNodeId ||
                    !nodesById.TryGetValue(destinationNodeId, out var destinationNode) ||
                    !sourceNode.OutputChannels.TryGetValue(connection.ChannelName, out var outputChannel))
                {
                    continue;
                }

                var explicitMappings = DeserializePropertyMappings(connection.PropertyMappingsJson);
                var effectiveMappings = explicitMappings.Count > 0
                    ? explicitMappings
                    : BuildDefaultPropertyMappings(outputChannel.Properties, destinationNode.InputChannel.Properties);

                var binding = new ExecutableBlueprintBinding
                {
                    Id = connection.Id.ToString("N"),
                    SourceBlueprintNodeId = sourceNode.Id,
                    SourceChannelName = outputChannel.Name,
                    DestinationBlueprintNodeId = destinationNode.Id,
                    PropertyMappings = effectiveMappings
                };

                outputChannel.OutboundBindings.Add(binding);
                destinationNode.InboundBindings.Add(binding);
            }
        }

        var allNodes = nodesById.Values
            .OrderBy(node => node.ExecutionOrder)
            .ThenBy(node => node.Id)
            .ToList();

        return new ExecutableBlueprint
        {
            Id = blueprintVersionEntity.Id,
            DefinitionId = blueprintVersionEntity.BlueprintDefinitionId,
            VersionNumber = blueprintVersionEntity.Version,
            Name = blueprintVersionEntity.BlueprintDefinition.DisplayName,
            Description = string.Empty,
            InitialNode = nodesById[blueprintVersionEntity.StartingBlueprintNodeId!.Value],
            AllNodes = allNodes
        };
    }

    public async Task<int> GetLatestBlueprintVersionId(
        int blueprintDefinitionId,
        CancellationToken cancellationToken)
    {
        var versionId = await _context.BlueprintVersions
            .AsNoTracking()
            .Where(version => version.BlueprintDefinitionId == blueprintDefinitionId)
            .OrderByDescending(version => version.Version)
            .Select(version => (int?)version.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return versionId
            ?? throw new EntityNotFoundException<BlueprintViewModel>(blueprintDefinitionId);
    }

    public async Task<List<BlueprintViewModel>> GetAllBlueprints(CancellationToken cancellationToken)
    {
        var items = await _context.BlueprintDefinitions
            .Include(definition => definition.Versions)
                .ThenInclude(version => version.BlueprintNodes)
                    .ThenInclude(node => node.TaskDefinition)
                        .ThenInclude(taskDefinition => taskDefinition.Versions)
                            .ThenInclude(taskVersion => taskVersion.Channels)
                                .ThenInclude(channel => channel.Properties)
            .Include(definition => definition.Versions)
                .ThenInclude(version => version.BlueprintNodes)
                    .ThenInclude(node => node.OutgoingConnections)
            .AsNoTracking()
            .AsSplitQuery()
            .OrderBy(definition => definition.Id)
            .ToListAsync(cancellationToken);

        return items.Select(item => MapBlueprintDefinition(item, includeTaskDetails: true)).ToList();
    }

    public async Task<BlueprintViewModel> GetBlueprintById(int id, CancellationToken cancellationToken)
    {
        var entity = await _context.BlueprintDefinitions
            .Include(definition => definition.Versions)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken);

        if (entity is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(id);
        }

        var versions = new List<BlueprintVersionViewModel>(entity.Versions.Count);
        foreach (var versionId in entity.Versions.OrderByDescending(version => version.Version).Select(version => version.Id))
        {
            versions.Add(await GetBlueprintVersionViewModel(versionId, cancellationToken));
        }

        return new BlueprintViewModel
        {
            Id = entity.Id,
            Key = entity.Key,
            Name = entity.DisplayName,
            Description = string.Empty,
            Versions = versions
        };
    }

    public async Task<BlueprintViewModel> CreateBlueprint(BlueprintInputModel model, CancellationToken cancellationToken)
    {
        var displayName = model.Name.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidDataException("Blueprint name is required.");
        }

        string key;
        if (string.IsNullOrWhiteSpace(model.Key))
        {
            key = await GenerateUniqueBlueprintKey(displayName, cancellationToken);
        }
        else
        {
            key = model.Key.Trim();
            var keyExists = await _context.BlueprintDefinitions
                .AnyAsync(definition => definition.Key == key, cancellationToken);
            if (keyExists)
            {
                throw new InvalidDataException($"Blueprint key '{key}' already exists.");
            }
        }

        var definition = new BlueprintDefinition
        {
            Key = key,
            DisplayName = displayName,
        };

        var blueprintVersion = await AddVersionEntities(
            definition,
            versionNumber: 1,
            startingBlueprintNodeId: model.StartingBlueprintNodeId,
            tasks: model.Tasks,
            cancellationToken);

        _context.BlueprintDefinitions.Add(definition);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        await _context.BlueprintVersions
            .Where(version => version.Id == blueprintVersion.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    version => version.StartingBlueprintNodeId,
                    model.StartingBlueprintNodeId),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetBlueprintById(definition.Id, cancellationToken);
    }

    public async Task<BlueprintVersionViewModel> CreateVersion(
        BlueprintVersionInputModel model,
        CancellationToken cancellationToken)
    {
        var definition = await _context.BlueprintDefinitions
            .Include(candidate => candidate.Versions)
            .FirstOrDefaultAsync(candidate => candidate.Id == model.BlueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(model.BlueprintDefinitionId);
        }

        var nextVersion = definition.Versions.Count == 0 ? 1 : definition.Versions.Max(version => version.Version) + 1;
        var createdVersion = await AddVersionEntities(
            definition,
            nextVersion,
            model.StartingBlueprintNodeId,
            model.Tasks,
            cancellationToken);

        _context.BlueprintVersions.Add(createdVersion);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        await _context.BlueprintVersions
            .Where(version => version.Id == createdVersion.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    version => version.StartingBlueprintNodeId,
                    model.StartingBlueprintNodeId),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetBlueprintVersionViewModel(createdVersion.Id, cancellationToken);
    }

    private async Task<BlueprintVersion> AddVersionEntities(
        BlueprintDefinition definition,
        int versionNumber,
        Guid startingBlueprintNodeId,
        List<BlueprintNodeInputModel> tasks,
        CancellationToken cancellationToken)
    {
        var blueprintVersion = new BlueprintVersion
        {
            BlueprintDefinition = definition,
            Version = versionNumber,
        };

        var instancesById = new Dictionary<Guid, BlueprintNode>(tasks.Count);
        foreach (var task in tasks)
        {
            var instanceId = task.InstanceId == Guid.Empty ? Guid.NewGuid() : task.InstanceId;
            var entity = new BlueprintNode
            {
                Id = instanceId,
                BlueprintVersion = blueprintVersion,
                TaskDefinitionId = task.TaskDefinitionId,
                AllowedMajor = task.AllowedMajor,
                AllowedMinor = task.AllowedMinor,
                PositionX = task.Position.X,
                PositionY = task.Position.Y,
                DesignTimeInputOverridesJson = SerializeJsonMap(task.DesignTimeInputOverrides),
            };

            blueprintVersion.BlueprintNodes.Add(entity);
            instancesById[entity.Id] = entity;
        }

        if (!instancesById.ContainsKey(startingBlueprintNodeId))
        {
            throw new InvalidDataException("StartingBlueprintNodeId must reference a task in tasks[].");
        }

        foreach (var task in tasks)
        {
            if (!instancesById.TryGetValue(task.InstanceId, out var sourceEntity))
            {
                continue;
            }

            foreach (var connection in task.OutgoingConnections)
            {
                if (!instancesById.TryGetValue(connection.ConnectedBlueprintNodeId, out var destinationEntity))
                {
                    continue;
                }

                var channelName = connection.ChannelName;
                if (string.IsNullOrWhiteSpace(channelName) && connection.ChannelId is not null)
                {
                    channelName = await _context.Channels
                        .Where(channel => channel.Id == connection.ChannelId.Value)
                        .Select(channel => channel.Name)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                if (string.IsNullOrWhiteSpace(channelName))
                {
                    continue;
                }

                sourceEntity.OutgoingConnections.Add(new BlueprintConnection
                {
                    Id = Guid.NewGuid(),
                    SourceBlueprintNode = sourceEntity,
                    DestinationBlueprintNode = destinationEntity,
                    Anchor = connection.Anchor,
                    Side = connection.Side,
                    Position = connection.Position,
                    ChannelName = channelName,
                    PropertyMappingsJson = SerializePropertyMappings(connection.PropertyMappings)
                });
            }
        }

        definition.Versions.Add(blueprintVersion);
        return blueprintVersion;
    }

    private async Task<string> GenerateUniqueBlueprintKey(string displayName, CancellationToken cancellationToken)
    {
        var baseKey = SlugifyBlueprintKey(displayName);
        if (string.IsNullOrWhiteSpace(baseKey))
        {
            baseKey = "blueprint";
        }

        var existingKeys = await _context.BlueprintDefinitions
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

    private static string SlugifyBlueprintKey(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = Regex.Replace(normalized, @"[^a-z0-9]+", "-");
        normalized = normalized.Trim('-');
        return normalized;
    }

    private BlueprintViewModel MapBlueprintDefinition(BlueprintDefinition entity, bool includeTaskDetails = false) =>
        new()
        {
            Id = entity.Id,
            Key = entity.Key,
            Name = entity.DisplayName,
            Description = string.Empty,
            Versions = entity.Versions
                .OrderByDescending(version => version.Version)
                .Select(version => MapBlueprintVersion(version, includeTaskDetails))
                .ToList()
        };

    private BlueprintVersionViewModel MapBlueprintVersion(BlueprintVersion version, bool includeTaskDetails) =>
        new()
        {
            BlueprintVersionId = version.Id,
            BlueprintDefinitionId = version.BlueprintDefinitionId,
            Description = null,
            Version = version.Version,
            StartingBlueprintNodeId = version.StartingBlueprintNodeId ?? Guid.Empty,
            Tasks = includeTaskDetails
                ? version.BlueprintNodes
                    .OrderBy(taskInstance => taskInstance.Id)
                    .Select(MapBlueprintTask)
                    .ToList()
                : new()
        };

    private BlueprintNodeViewModel MapBlueprintTask(BlueprintNode taskInstance)
    {
        var resolvedVersion = ResolveConcreteVersion(taskInstance, requireActive: false);
        return new BlueprintNodeViewModel
        {
            InstanceId = taskInstance.Id,
            TaskDefinitionId = taskInstance.TaskDefinitionId,
            AllowedMajor = taskInstance.AllowedMajor,
            AllowedMinor = taskInstance.AllowedMinor,
            ResolvedVersionId = resolvedVersion.Id,
            ResolvedVersion = resolvedVersion.Version.ToString(),
            Position = new CoordinatesViewModel { X = taskInstance.PositionX, Y = taskInstance.PositionY },
            DesignTimeInputOverrides = DeserializeJsonMap(taskInstance.DesignTimeInputOverridesJson),
            OutgoingConnections = taskInstance.OutgoingConnections
                .Select(connection => new BlueprintConnectionViewModel
                {
                    ChannelId = resolvedVersion.OutputChannels.FirstOrDefault(channel => channel.Name == connection.ChannelName)?.Id,
                    ChannelName = connection.ChannelName,
                    BlueprintNodeId = connection.DestinationBlueprintNodeId ?? Guid.Empty,
                    Anchor = connection.Anchor,
                    Side = connection.Side,
                    Position = connection.Position,
                    PropertyMappings = DeserializePropertyMappings(connection.PropertyMappingsJson)
                        .Select(mapping => new BlueprintPropertyMappingViewModel
                        {
                            SourceProperty = mapping.SourceProperty,
                            TargetProperty = mapping.TargetProperty
                        })
                        .ToList()
                })
                .Where(connection => connection.BlueprintNodeId != Guid.Empty)
                .ToList()
        };
    }

    private async Task<BlueprintVersionViewModel> GetBlueprintVersionViewModel(
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var blueprintVersion = await _context.BlueprintVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(version => version.Id == blueprintVersionId, cancellationToken);

        if (blueprintVersion is null)
        {
            throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersionId);
        }

        var taskInstances = await _context.BlueprintNodes
            .Where(taskInstance => taskInstance.BlueprintVersionId == blueprintVersionId)
            .Include(taskInstance => taskInstance.TaskDefinition)
                .ThenInclude(taskDefinition => taskDefinition.Versions)
                    .ThenInclude(taskVersion => taskVersion.Channels)
                        .ThenInclude(channel => channel.Properties)
            .Include(taskInstance => taskInstance.OutgoingConnections)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        blueprintVersion.BlueprintNodes = taskInstances;
        return MapBlueprintVersion(blueprintVersion, includeTaskDetails: true);
    }

    private static Dictionary<Guid, int> ComputeExecutionOrder(
        BlueprintVersion blueprintVersion,
        IReadOnlyCollection<BlueprintNode> nodeEntities)
    {
        var outgoingByNodeId = nodeEntities.ToDictionary(
            node => node.Id,
            node => node.OutgoingConnections
                .Where(connection => connection.DestinationBlueprintNodeId.HasValue)
                .Select(connection => connection.DestinationBlueprintNodeId!.Value)
                .ToList());

        var visited = new HashSet<Guid>();
        var order = new List<Guid>(nodeEntities.Count);

        void Visit(Guid nodeId)
        {
            if (!visited.Add(nodeId))
            {
                return;
            }

            foreach (var destinationId in outgoingByNodeId[nodeId].OrderBy(id => id))
            {
                Visit(destinationId);
            }

            order.Add(nodeId);
        }

        if (blueprintVersion.StartingBlueprintNodeId is Guid startingNodeId)
        {
            Visit(startingNodeId);
        }

        foreach (var remainingNodeId in nodeEntities
                     .Select(node => node.Id)
                     .Where(nodeId => !visited.Contains(nodeId))
                     .OrderBy(nodeId => nodeId))
        {
            Visit(remainingNodeId);
        }

        order.Reverse();
        return order
            .Select((nodeId, index) => new { nodeId, index })
            .ToDictionary(item => item.nodeId, item => item.index);
    }

    private static List<ExecutableBlueprintPropertyMapping> BuildDefaultPropertyMappings(
        IReadOnlyDictionary<string, string> sourceProperties,
        IReadOnlyDictionary<string, string> targetProperties) =>
        sourceProperties.Keys
            .Intersect(targetProperties.Keys, StringComparer.Ordinal)
            .OrderBy(property => property, StringComparer.Ordinal)
            .Select(property => new ExecutableBlueprintPropertyMapping
            {
                SourceProperty = property,
                TargetProperty = property
            })
            .ToList();

    private static string? SerializeJsonMap(Dictionary<string, JsonElement> values) =>
        values.Count == 0
            ? null
            : JsonSerializer.Serialize(values, JsonOptions);

    private static Dictionary<string, JsonElement> DeserializeJsonMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOptions) ?? new();
        return parsed.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static string? SerializePropertyMappings(IReadOnlyCollection<BlueprintPropertyMappingInputModel> mappings) =>
        mappings.Count == 0
            ? null
            : JsonSerializer.Serialize(
                mappings.Select(mapping => new SerializablePropertyMapping(mapping.SourceProperty, mapping.TargetProperty)).ToList(),
                JsonOptions);

    private static List<ExecutableBlueprintPropertyMapping> DeserializePropertyMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return (JsonSerializer.Deserialize<List<SerializablePropertyMapping>>(json, JsonOptions) ?? [])
            .Select(mapping => new ExecutableBlueprintPropertyMapping
            {
                SourceProperty = mapping.SourceProperty,
                TargetProperty = mapping.TargetProperty
            })
            .ToList();
    }

    private static TaskVersion ResolveConcreteVersion(BlueprintNode taskInstance, bool requireActive)
    {
        var laneVersions = taskInstance.TaskDefinition.Versions
            .Where(version => SemanticVersionUtility.IsSameLane(version.Version, taskInstance.AllowedMajor, taskInstance.AllowedMinor))
            .OrderByDescending(version => version.Version)
            .ToList();

        if (laneVersions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No task version exists for task definition {taskInstance.TaskDefinitionId} lane {taskInstance.AllowedMajor}.{taskInstance.AllowedMinor}.");
        }

        _ = requireActive;
        return laneVersions.First();
    }

    private sealed record SerializablePropertyMapping(string SourceProperty, string TargetProperty);
}
