using Backend.DTOs;
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

            InitialTask = taskDtosById[blueprintVersionEntity.StartingTaskInstanceId],

            AllTasks = allTasks
        };

        return workflowBlueprintDto;
    }
}
