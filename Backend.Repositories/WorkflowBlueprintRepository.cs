using Backend.DTOs;
using Backend.Interfaces.Repositories;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore;

namespace Backend.Repositories;

public class WorkflowBlueprintRepository : IWorkflowBlueprintRepository
{
    private readonly WorkflowDbContext _context;

    public WorkflowBlueprintRepository(WorkflowDbContext context)
    {
        _context = context;
    }

    public async Task<WorkflowBlueprint> GetFullBlueprintById(
        int id, CancellationToken cancellationToken)
    {
        var blueprintEntity = await _context.WorkflowBlueprints
            .Include(wbp => wbp.Tasks)
                .ThenInclude(t => t.InputChannel.Schema!.Properties)
            .Include(wbp => wbp.Tasks)
                .ThenInclude(t => t.OutputChannels)
                .ThenInclude(oc => oc.Schema!.Properties)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(wbp => wbp.Id == id,
                cancellationToken);

        if (blueprintEntity is null)
            throw new EntityNotFoundException<WorkflowBlueprint>(id);

        var taskDtos = new List<WorkflowTask>();

        foreach (var taskEntity in blueprintEntity.Tasks)
        {
            var channelEntitiesBySpecId = new Dictionary<int, WorkflowTaskChannel>
            {
                {
                    taskEntity.InputChannel.Schema!.Id,
                    new WorkflowTaskChannel
                    {
                        Id = taskEntity.InputChannel.Id,
                        Name = taskEntity.InputChannel.Schema!.Name,
                        Criterion = taskEntity.InputChannel.Schema!.Properties.ToDictionary(p => p.Name, p => p.Type)
                    }
                }
            };

            foreach (var channelEntity in taskEntity.OutputChannels)
            {
                channelEntitiesBySpecId.Add(channelEntity.Schema!.Id, new WorkflowTaskChannel
                {
                    Id = channelEntity.Id,
                    Name = channelEntity.Schema!.Name,
                    Criterion = channelEntity.Schema!.Properties.ToDictionary(p => p.Name, p => p.Type)
                });
            }

            taskDtos.Add(new WorkflowTask
            {
                Id = taskEntity.Id,
                Name = taskEntity.Schema!.Name,
                Version = taskEntity.Schema!.Version,
                SchemaId = taskEntity.Schema!.Id,
                InputChannel = channelEntitiesBySpecId[taskEntity.Schema.InputChannelSpecificationId!.Value]!,
                OutputChannels = taskEntity.Schema.OutputChannelSpecifications
                    .ToDictionary(ocs => ocs.Name, ocs => channelEntitiesBySpecId[ocs.Id])
            });
        }

        foreach (var taskDto in taskDtos)
        {
            var taskEntity = blueprintEntity.Tasks
                .First(t => t.Id == taskDto.Id);

            /*foreach (var channel in taskDto.OutputChannels.Values)
            {
                channel.Source = taskDtos.First(t => t.Id == taskEntity.InputChannel.SourceNodeId);
                channel.Destination = taskDtos.First(t => t.Id ==
                    taskEntity.OutputChannels.First(c => c.Id == channel.Id).DestinationNodeId);
            }*/
        }

        var workflowBlueprintDto = new WorkflowBlueprint
        {
            Id = blueprintEntity.Id,
            Name = blueprintEntity.Name,
            Description = blueprintEntity.Description,
            InitialTask = taskDtos.First(t => t.Id == blueprintEntity.StartingTaskId),
            AllTasks = taskDtos
        };

        return workflowBlueprintDto;
    }
}
