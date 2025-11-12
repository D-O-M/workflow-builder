using Backend.Relational.DAL;
using Backend.DTOs;
using Backend.Interfaces.Repositories;
using Backend.Mappers;
using Microsoft.EntityFrameworkCore;
using DAL = Backend.Relational.DAL;

namespace Backend.Repositories;

public class WorkflowTaskRepository : IWorkflowTaskRepository
{
    private readonly WorkflowDbContext _context;

    public WorkflowTaskRepository(WorkflowDbContext context)
    {
        _context = context;
    }

    public async Task<List<WorkflowTaskInfo>> GetAll(CancellationToken cancellationToken) =>
        await _context.TaskSchemas
            .ProjectToWorkflowTaskInfoDto()
            .ToListAsync(cancellationToken);

    public async Task CreateTaskSchema(Manifest manifest, CancellationToken cancellationToken)
    {
        var workflowTask = new TaskSchema
        {
            Name = manifest.Name,
            Path = manifest.Path,
            Version = manifest.Version
        };

        foreach (var channel in manifest.Channels)
        {
            DAL.ChannelSchema newChannel = new DAL.ChannelSchema
            {
                Name = channel.Name
            };

            foreach (var parameter in channel.Parameters)
            {
                newChannel.Parameters.Add(new()
                {
                    Name = parameter.Name,
                    Type = parameter.Type
                });
            }

            if (channel.ChannelType is ManifestChannelType.Input)
                workflowTask.InputChannels.Add(newChannel);
            else if (channel.ChannelType is ManifestChannelType.Output)
                workflowTask.OutputChannels.Add(newChannel);
        }

        _context.Add(workflowTask);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
