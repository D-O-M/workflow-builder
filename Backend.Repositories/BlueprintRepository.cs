using Backend.Models;
using Backend.Models.DomainModels;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Interfaces.Repositories;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Microsoft.EntityFrameworkCore;
using Backend.Models.Versioning;
using System.Text.RegularExpressions;

namespace Backend.Repositories;

public class BlueprintRepository : IBlueprintRepository
{
    private readonly BlueprintDbContext _context;

    public BlueprintRepository(BlueprintDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Loads a single Blueprint *version* (BV) by id, including:
    /// - BlueprintNodes (TI) (nodes)
    /// - TaskVersion (TV) + its Input/Output Channels + ChannelProperties
    /// - BlueprintConnections (TC) + their Channel + ChannelProperties
    /// </summary>
    public async Task<ExecutableBlueprint> GetFullBlueprintById(
        int id, CancellationToken cancellationToken)
    {
        var blueprintVersionEntity = await _context.BlueprintVersions
            .AsNoTracking()
            .Include(version => version.BlueprintDefinition)
            .FirstOrDefaultAsync(bv => bv.Id == id, cancellationToken);

        if (blueprintVersionEntity is null)
            throw new EntityNotFoundException<ExecutableBlueprint>(id);

        // Load the task graph separately using the same include shape that backs blueprint reads.
        // This avoids partial task-definition version materialization during process-time resolution.
        var taskInstances = await _context.BlueprintNodes
            .Where(taskInstance => taskInstance.BlueprintVersionId == id)
            .Include(taskInstance => taskInstance.TaskDefinition)
                .ThenInclude(taskDefinition => taskDefinition.Versions)
                    .ThenInclude(taskVersion => taskVersion.Channels)
                        .ThenInclude(channel => channel.Properties)
            .Include(taskInstance => taskInstance.OutgoingConnections)
            .Include(taskInstance => taskInstance.OutgoingConnections)
                .ThenInclude(connection => connection.DestinationBlueprintNode)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        blueprintVersionEntity.BlueprintNodes = taskInstances;

        // Build DTOs for each node (BlueprintNode)
        var blueprintNodeDtosById = new Dictionary<Guid, ExecutableBlueprintNode>(capacity: blueprintVersionEntity.BlueprintNodes.Count);

        foreach (var nodeEntity in blueprintVersionEntity.BlueprintNodes)
        {
            var taskVersion = ResolveConcreteVersion(nodeEntity, requireActive: true);

            // Your new schema supports multiple input channels; if your domain expects exactly one,
            // keep Single(). If it can be 0/1+, adjust the DTO accordingly.
            var inputChannelEntity = taskVersion.InputChannels.Single();

            // Build input channel DTO
            var inputChannelDto = new ExecutableBlueprintChannel
            {
                Id = inputChannelEntity.Id,
                Name = inputChannelEntity.Name,
                Criterion = inputChannelEntity.Properties.ToDictionary(p => p.Name, p => p.Type.ToString())
            };

            // Build output channel DTOs keyed by channel name (matches your old style)
            var outputChannelDtos = taskVersion.OutputChannels.ToDictionary(
                oc => oc.Name,
                oc => new ExecutableBlueprintChannel
                {
                    Id = oc.Id,
                    Name = oc.Name,
                    Criterion = oc.Properties.ToDictionary(p => p.Name, p => p.Type.ToString())
                });

            // Node DTO (ExecutableBlueprintNode) – Id becomes BlueprintNode.Id (Guid)
            var blueprintNodeDto = new ExecutableBlueprintNode
            {
                Id = nodeEntity.Id,
                TaskDefinitionId = nodeEntity.TaskDefinitionId,
                AllowedMajor = nodeEntity.AllowedMajor,
                AllowedMinor = nodeEntity.AllowedMinor,
                Name = taskVersion.TaskDefinition!.Name,
                ResolvedVersion = taskVersion.Version,

                ResolvedTaskVersionId = taskVersion.Id,

                InputChannel = inputChannelDto,
                OutputChannels = outputChannelDtos
            };

            blueprintNodeDtosById.Add(blueprintNodeDto.Id, blueprintNodeDto);
        }

        foreach (var nodeEntity in blueprintVersionEntity.BlueprintNodes)
        {
            var sourceBlueprintNodeDto = blueprintNodeDtosById[nodeEntity.Id];
            var outputChannelDtosByName = sourceBlueprintNodeDto.OutputChannels;

            foreach (var connection in nodeEntity.OutgoingConnections)
            {
                if (connection.DestinationBlueprintNodeId is null)
                {
                    continue;
                }

                if (!blueprintNodeDtosById.TryGetValue(connection.DestinationBlueprintNodeId.Value, out var destinationBlueprintNodeDto))
                {
                    continue;
                }

                if (!outputChannelDtosByName.TryGetValue(connection.ChannelName, out var outputChannelDto))
                {
                    continue;
                }

                outputChannelDto.Source = sourceBlueprintNodeDto;
                outputChannelDto.Destination = destinationBlueprintNodeDto;
            }
        }

        var allNodes = blueprintNodeDtosById.Values.ToList();

        var executableBlueprint = new ExecutableBlueprint
        {
            // Since you asked for “singular version of a blueprint”, this is BV.Id.
            Id = blueprintVersionEntity.Id,
            DefinitionId = blueprintVersionEntity.BlueprintDefinitionId,
            VersionNumber = blueprintVersionEntity.Version,

            Name = blueprintVersionEntity.BlueprintDefinition.DisplayName,

            // Old code referenced Description; your DBML no longer has it.
            // If DTO requires it, populate empty/placeholder or pull from BlueprintDefinition if you add it.
            Description = string.Empty,

            InitialNode = blueprintNodeDtosById[blueprintVersionEntity.StartingBlueprintNodeId!.Value],
            AllNodes = allNodes
        };

        return executableBlueprint;
    }

    public async Task<List<BlueprintViewModel>> GetAllBlueprints(CancellationToken cancellationToken)
    {
        var items = await _context.BlueprintDefinitions
            .Include(d => d.Versions)
                .ThenInclude(version => version.BlueprintNodes)
                    .ThenInclude(taskInstance => taskInstance.TaskDefinition)
                        .ThenInclude(taskDefinition => taskDefinition.Versions)
                            .ThenInclude(taskVersion => taskVersion.Channels)
            .Include(d => d.Versions)
                .ThenInclude(version => version.BlueprintNodes)
                    .ThenInclude(taskInstance => taskInstance.OutgoingConnections)
            .AsNoTracking()
            .AsSplitQuery()
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);

        return items.Select(item => MapBlueprintDefinition(item, includeTaskDetails: true)).ToList();
    }

    public async Task<BlueprintViewModel> GetBlueprintById(int id, CancellationToken cancellationToken)
    {
        var entity = await _context.BlueprintDefinitions
            .Include(d => d.Versions)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (entity is null)
            throw new EntityNotFoundException<BlueprintViewModel>(id);

        var versions = new List<BlueprintVersionViewModel>(entity.Versions.Count);
        foreach (var versionId in entity.Versions.OrderByDescending(v => v.Version).Select(v => v.Id))
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

        // Definition "Key" is currently required and unique; use a deterministic key.
        // If you later add a dedicated "Name" column, change this mapping.
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

        // Reload for a consistent view model (including generated ids)
        return await GetBlueprintById(definition.Id, cancellationToken);
    }

    public async Task<BlueprintVersionViewModel> CreateVersion(FullBlueprintVersion model, CancellationToken cancellationToken)
    {
        var definition = await _context.BlueprintDefinitions
            .Include(d => d.Versions)
            .FirstOrDefaultAsync(d => d.Id == model.DefinitionId, cancellationToken);

        if (definition is null)
            throw new EntityNotFoundException<BlueprintViewModel>(model.DefinitionId);

        var nextVersion = definition.Versions.Count == 0 ? 1 : definition.Versions.Max(v => v.Version) + 1;

            var tasks = model.TasksByInstanceId.Values
            .Select(t => new BlueprintNodeInputModel
            {
                InstanceId = t.InstanceId,
                TaskDefinitionId = t.TaskDefinitionId,
                AllowedMajor = t.AllowedMajor,
                AllowedMinor = t.AllowedMinor,
                Position = new CoordinatesViewModel { X = t.Position.X, Y = t.Position.Y },
                OutgoingConnections = t.OutgoingConnections.Select(c => new BlueprintConnectionInputModel
                {
                    ChannelId = c.ChannelDetails.ChannelId,
                    ChannelName = c.ChannelDetails.ChannelName,
                    ConnectedBlueprintNodeId = c.Task?.InstanceId ?? Guid.Empty,
                    Anchor = c.Anchor,
                    Side = c.Side,
                    Position = c.Position
                }).Where(c => c.ConnectedBlueprintNodeId != Guid.Empty).ToList()
            })
            .ToList();

        var createdVersion = await AddVersionEntities(
            definition,
            nextVersion,
            model.TaskTree.InstanceId,
            tasks,
            cancellationToken);

        _context.BlueprintVersions.Add(createdVersion);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        await _context.BlueprintVersions
            .Where(version => version.Id == createdVersion.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    version => version.StartingBlueprintNodeId,
                    model.TaskTree.InstanceId),
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

        // BlueprintNodes
        var instancesById = new Dictionary<Guid, BlueprintNode>(tasks.Count);
        foreach (var t in tasks)
        {
            var instanceId = t.InstanceId == Guid.Empty ? Guid.NewGuid() : t.InstanceId;
            var entity = new BlueprintNode
            {
                Id = instanceId,
                BlueprintVersion = blueprintVersion,
                TaskDefinitionId = t.TaskDefinitionId,
                AllowedMajor = t.AllowedMajor,
                AllowedMinor = t.AllowedMinor,
                PositionX = t.Position.X,
                PositionY = t.Position.Y,
            };

            blueprintVersion.BlueprintNodes.Add(entity);
            instancesById[entity.Id] = entity;
        }

        if (!instancesById.ContainsKey(startingBlueprintNodeId))
            throw new InvalidDataException("StartingBlueprintNodeId must reference a task in tasks[].");

        // Connections
        foreach (var t in tasks)
        {
            var sourceId = t.InstanceId;
            if (sourceId == Guid.Empty) continue;

            if (!instancesById.TryGetValue(sourceId, out var sourceEntity))
                continue;

            foreach (var conn in t.OutgoingConnections)
            {
                if (!instancesById.TryGetValue(conn.ConnectedBlueprintNodeId, out var destEntity))
                    continue;

                var channelName = conn.ChannelName;
                if (string.IsNullOrWhiteSpace(channelName) && conn.ChannelId is not null)
                {
                    channelName = await _context.Channels
                        .Where(c => c.Id == conn.ChannelId.Value)
                        .Select(c => c.Name)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                if (string.IsNullOrWhiteSpace(channelName))
                    continue;

                sourceEntity.OutgoingConnections.Add(new Backend.Database.Relational.Entities.BlueprintConnection
                {
                    Id = Guid.NewGuid(),
                    SourceBlueprintNode = sourceEntity,
                    DestinationBlueprintNode = destEntity,
                    Anchor = conn.Anchor,
                    Side = conn.Side,
                    Position = conn.Position,
                    ChannelName = channelName
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

    private BlueprintViewModel MapBlueprintDefinition(BlueprintDefinition entity, bool includeTaskDetails = false)
        => new BlueprintViewModel
        {
            Id = entity.Id,
            Key = entity.Key,
            Name = entity.DisplayName,
            Description = string.Empty,
            Versions = entity.Versions
                .OrderByDescending(v => v.Version)
                .Select(v => MapBlueprintVersion(v, includeTaskDetails))
                .ToList()
        };

    private BlueprintVersionViewModel MapBlueprintVersion(BlueprintVersion version, bool includeTaskDetails)
        => new()
        {
            BlueprintVersionId = version.Id,
            BlueprintDefinitionId = version.BlueprintDefinitionId,
            Description = null,
            Version = version.Version,
            StartingBlueprintNodeId = version.StartingBlueprintNodeId ?? Guid.Empty,
            Tasks = includeTaskDetails
                ? version.BlueprintNodes.OrderBy(taskInstance => taskInstance.Id).Select(MapBlueprintTask).ToList()
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
            OutgoingConnections = taskInstance.OutgoingConnections.Select(connection => new BlueprintConnectionViewModel
            {
                ChannelId = resolvedVersion.OutputChannels.FirstOrDefault(channel => channel.Name == connection.ChannelName)?.Id,
                ChannelName = connection.ChannelName,
                BlueprintNodeId = connection.DestinationBlueprintNodeId ?? Guid.Empty,
                Anchor = connection.Anchor,
                Side = connection.Side,
                Position = connection.Position,
            }).Where(connection => connection.BlueprintNodeId != Guid.Empty).ToList()
        };
    }

    private async Task<BlueprintVersionViewModel> GetBlueprintVersionViewModel(int blueprintVersionId, CancellationToken cancellationToken)
    {
        var bv = await _context.BlueprintVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == blueprintVersionId, cancellationToken);

        if (bv is null)
            throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersionId);

        var taskInstances = await _context.BlueprintNodes
            .Where(ti => ti.BlueprintVersionId == blueprintVersionId)
            .Include(ti => ti.TaskDefinition)
                .ThenInclude(td => td.Versions)
                    .ThenInclude(tv => tv.Channels)
            .Include(ti => ti.OutgoingConnections)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        bv.BlueprintNodes = taskInstances;

        return MapBlueprintVersion(bv, includeTaskDetails: true);
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
}
