using Backend.Models;
using Backend.Models.DomainModels;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Interfaces.Repositories;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Microsoft.EntityFrameworkCore;

namespace Backend.Repositories;

public class WorkflowBlueprintRepository : IWorkflowBlueprintRepository
{
    private readonly WorkflowDbContext _context;

    public WorkflowBlueprintRepository(WorkflowDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Loads a single Blueprint *version* (BV) by id, including:
    /// - TaskInstances (TI) (nodes)
    /// - TaskVersion (TV) + its Input/Output Channels + ChannelProperties
    /// - TaskConnections (TC) + their Channel + ChannelProperties
    /// </summary>
    public async Task<WorkflowBlueprint> GetFullBlueprintById(
        int id, CancellationToken cancellationToken)
    {
        // id == BlueprintVersion.Id (BV.Id)
        var blueprintVersionEntity = await _context.BlueprintVersions
            .Include(bv => bv.TaskInstances)
                .ThenInclude(ti => ti.TaskVersion.TaskDefinition)
            .Include(bv => bv.TaskInstances)
                .ThenInclude(ti => ti.TaskVersion)
                    .ThenInclude(tv => tv.Channels)
                        .ThenInclude(c => c.Properties)
            .Include(bv => bv.TaskInstances)
                .ThenInclude(ti => ti.OutgoingConnections)
                    .ThenInclude(tc => tc.Channel.Properties)
            // If you later want to traverse destinations in-memory without extra queries:
            .Include(bv => bv.TaskInstances)
                .ThenInclude(ti => ti.OutgoingConnections)
                    .ThenInclude(tc => tc.DestinationTaskInstance)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(bv => bv.Id == id, cancellationToken);

        if (blueprintVersionEntity is null)
            throw new EntityNotFoundException<WorkflowBlueprint>(id);

        // Build DTOs for each node (TaskInstance)
        var taskDtosById = new Dictionary<Guid, WorkflowTask>(capacity: blueprintVersionEntity.TaskInstances.Count);

        foreach (var nodeEntity in blueprintVersionEntity.TaskInstances)
        {
            var taskVersion = nodeEntity.TaskVersion;

            // Your new schema supports multiple input channels; if your domain expects exactly one,
            // keep Single(). If it can be 0/1+, adjust the DTO accordingly.
            var inputChannelEntity = taskVersion.InputChannels.Single();

            // Build input channel DTO
            var inputChannelDto = new WorkflowTaskChannel
            {
                Id = inputChannelEntity.Id,
                Name = inputChannelEntity.Name,
                Criterion = inputChannelEntity.Properties.ToDictionary(p => p.Name, p => p.Type.ToString())
            };

            // Build output channel DTOs keyed by channel name (matches your old style)
            var outputChannelDtos = taskVersion.OutputChannels.ToDictionary(
                oc => oc.Name,
                oc => new WorkflowTaskChannel
                {
                    Id = oc.Id,
                    Name = oc.Name,
                    Criterion = oc.Properties.ToDictionary(p => p.Name, p => p.Type.ToString())
                });

            // Node DTO (WorkflowTask) – Id becomes TaskInstance.Id (Guid)
            var taskDto = new WorkflowTask
            {
                Id = nodeEntity.Id,
                Name = taskVersion.TaskDefinition!.Name,
                Version = taskVersion.Version,

                // if your DTO still expects a schema id, TV.Id is the concrete version row
                SchemaId = taskVersion.Id,

                InputChannel = inputChannelDto,
                OutputChannels = outputChannelDtos
            };

            taskDtosById.Add(taskDto.Id, taskDto);
        }

        // OPTIONAL: If WorkflowTaskChannel has Source/Destination navigation in DTO-land,
        // you can wire it here using TaskConnections. This was commented out in your old code;
        // leaving it out keeps parity.
        //
        // foreach (var nodeEntity in blueprintVersionEntity.TaskInstances)
        // {
        //     var sourceTaskDto = taskDtosById[nodeEntity.Id];
        //
        //     foreach (var conn in nodeEntity.OutgoingConnections)
        //     {
        //         if (conn.DestinationTaskInstanceId is null) continue;
        //
        //         // Map connection.ChannelId -> the output channel DTO on the source task
        //         // (You currently key output channels by name; you could also build an id->dto lookup.)
        //     }
        // }

        var allTasks = taskDtosById.Values.ToList();

        var workflowBlueprintDto = new WorkflowBlueprint
        {
            // Since you asked for “singular version of a blueprint”, this is BV.Id.
            Id = blueprintVersionEntity.Id,

            Name = blueprintVersionEntity.Name,

            // Old code referenced Description; your DBML no longer has it.
            // If DTO requires it, populate empty/placeholder or pull from BlueprintDefinition if you add it.
            Description = string.Empty,

            InitialTask = taskDtosById[blueprintVersionEntity.StartingTaskInstanceId!.Value],

            AllTasks = allTasks
        };

        return workflowBlueprintDto;
    }

    public async Task<List<BlueprintViewModel>> GetAllBlueprints(CancellationToken cancellationToken)
    {
        var items = await _context.BlueprintDefinitions
            .Include(d => d.Versions)
            .AsNoTracking()
            .AsSplitQuery()
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);

        return items.Select(item => MapBlueprintDefinition(item)).ToList();
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
            Name = entity.Key,
            Description = string.Empty,
            Versions = versions
        };
    }

    public async Task<BlueprintViewModel> CreateBlueprint(BlueprintInputModel model, CancellationToken cancellationToken)
    {
        // Definition "Key" is currently required and unique; use a deterministic key.
        // If you later add a dedicated "Name" column, change this mapping.
        var definition = new BlueprintDefinition
        {
            Key = model.Name,
        };

        var blueprintVersion = await AddVersionEntities(
            definition,
            versionNumber: 1,
            name: model.Name,
            startingTaskInstanceId: model.StartingTaskInstanceId,
            tasks: model.Tasks,
            cancellationToken);

        _context.BlueprintDefinitions.Add(definition);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        blueprintVersion.StartingTaskInstanceId = model.StartingTaskInstanceId;
        await _context.SaveChangesAsync(cancellationToken);

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
            .Select(t => new BlueprintTaskInputModel
            {
                InstanceId = t.InstanceId,
                VersionId = t.VersionId,
                Position = new CoordinatesViewModel { X = t.Position.X, Y = t.Position.Y },
                OutgoingConnections = t.OutgoingConnections.Select(c => new BlueprintTaskConnectionInputModel
                {
                    ChannelId = c.ChannelDetails.ChannelId,
                    ChannelName = c.ChannelDetails.ChannelName,
                    ConnectedTaskInstanceId = c.Task?.InstanceId ?? Guid.Empty,
                    Anchor = c.Anchor,
                    Side = c.Side,
                    Position = c.Position
                }).Where(c => c.ConnectedTaskInstanceId != Guid.Empty).ToList()
            })
            .ToList();

        var createdVersion = await AddVersionEntities(
            definition,
            nextVersion,
            model.Name,
            model.TaskTree.InstanceId,
            tasks,
            cancellationToken);

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        createdVersion.StartingTaskInstanceId = model.TaskTree.InstanceId;
        await _context.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return await GetBlueprintVersionViewModel(createdVersion.Id, cancellationToken);
    }

    private async Task<BlueprintVersion> AddVersionEntities(
        BlueprintDefinition definition,
        int versionNumber,
        string name,
        Guid startingTaskInstanceId,
        List<BlueprintTaskInputModel> tasks,
        CancellationToken cancellationToken)
    {
        var blueprintVersion = new BlueprintVersion
        {
            BlueprintDefinition = definition,
            Version = versionNumber,
            Name = name,
        };

        // TaskInstances
        var instancesById = new Dictionary<Guid, TaskInstance>(tasks.Count);
        foreach (var t in tasks)
        {
            var instanceId = t.InstanceId == Guid.Empty ? Guid.NewGuid() : t.InstanceId;
            var entity = new TaskInstance
            {
                Id = instanceId,
                BlueprintVersion = blueprintVersion,
                TaskVersionId = t.VersionId,
                PositionX = t.Position.X,
                PositionY = t.Position.Y,
            };

            blueprintVersion.TaskInstances.Add(entity);
            instancesById[entity.Id] = entity;
        }

        if (!instancesById.ContainsKey(startingTaskInstanceId))
            throw new InvalidDataException("StartingTaskInstanceId must reference a task in tasks[].");

        // Connections
        foreach (var t in tasks)
        {
            var sourceId = t.InstanceId;
            if (sourceId == Guid.Empty) continue;

            if (!instancesById.TryGetValue(sourceId, out var sourceEntity))
                continue;

            foreach (var conn in t.OutgoingConnections)
            {
                if (!instancesById.TryGetValue(conn.ConnectedTaskInstanceId, out var destEntity))
                    continue;

                var channelId = conn.ChannelId;
                if (channelId is null)
                {
                    // If only the channel name is supplied, resolve it against the source task version.
                    if (string.IsNullOrWhiteSpace(conn.ChannelName))
                        continue;

                    channelId = await _context.Channels
                        .Where(c => c.TaskVersionId == sourceEntity.TaskVersionId && c.Name == conn.ChannelName)
                        .Select(c => (int?)c.Id)
                        .FirstOrDefaultAsync(cancellationToken);
                }

                if (channelId is null)
                    continue;

                sourceEntity.OutgoingConnections.Add(new Backend.Database.Relational.Entities.TaskConnection
                {
                    Id = Guid.NewGuid(),
                    SourceTaskInstance = sourceEntity,
                    DestinationTaskInstance = destEntity,
                    Anchor = conn.Anchor,
                    Side = conn.Side,
                    Position = conn.Position,
                    ChannelId = channelId.Value
                });
            }
        }

        definition.Versions.Add(blueprintVersion);
        return blueprintVersion;
    }

    private static BlueprintViewModel MapBlueprintDefinition(BlueprintDefinition entity, bool includeTaskDetails = false)
        => new BlueprintViewModel
        {
            Id = entity.Id,
            Name = entity.Key,
            Description = string.Empty,
            Versions = entity.Versions
                .OrderByDescending(v => v.Version)
                .Select(v => MapBlueprintVersion(v, includeTaskDetails))
                .ToList()
        };

    private static BlueprintVersionViewModel MapBlueprintVersion(BlueprintVersion version, bool includeTaskDetails)
        => new()
        {
            BlueprintVersionId = version.Id,
            BlueprintDefinitionId = version.BlueprintDefinitionId,
            Name = version.Name,
            Description = null,
            Version = version.Version,
            StartingTaskInstanceId = version.StartingTaskInstanceId ?? Guid.Empty,
            Tasks = includeTaskDetails
                ? version.TaskInstances.OrderBy(taskInstance => taskInstance.Id).Select(MapBlueprintTask).ToList()
                : new()
        };

    private static BlueprintTaskViewModel MapBlueprintTask(TaskInstance taskInstance)
        => new()
        {
            InstanceId = taskInstance.Id,
            VersionId = taskInstance.TaskVersionId,
            Position = new CoordinatesViewModel { X = taskInstance.PositionX, Y = taskInstance.PositionY },
            OutgoingConnections = taskInstance.OutgoingConnections.Select(connection => new BlueprintTaskConnectionViewModel
            {
                ChannelId = connection.ChannelId,
                ChannelName = connection.Channel?.Name,
                TaskInstanceId = connection.DestinationTaskInstanceId ?? Guid.Empty,
                Anchor = connection.Anchor,
                Side = connection.Side,
                Position = connection.Position,
            }).Where(connection => connection.TaskInstanceId != Guid.Empty).ToList()
        };

    private async Task<BlueprintVersionViewModel> GetBlueprintVersionViewModel(int blueprintVersionId, CancellationToken cancellationToken)
    {
        var bv = await _context.BlueprintVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == blueprintVersionId, cancellationToken);

        if (bv is null)
            throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersionId);

        var taskInstances = await _context.TaskInstances
            .Where(ti => ti.BlueprintVersionId == blueprintVersionId)
            .Include(ti => ti.OutgoingConnections)
                .ThenInclude(tc => tc.Channel)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        bv.TaskInstances = taskInstances;

        return MapBlueprintVersion(bv, includeTaskDetails: true);
    }
}
