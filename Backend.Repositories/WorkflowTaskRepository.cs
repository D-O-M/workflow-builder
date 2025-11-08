using Backend.Conformation.DAL;
using Backend.DTOs;
using Backend.Interfaces.Repositories;
using Backend.Mappers;
using Microsoft.EntityFrameworkCore;
using DAL = Backend.Conformation.DAL;

namespace Backend.Repositories;

public class WorkflowTaskRepository : IWorkflowTaskRepository
{
    private readonly WorkflowDbContext _context;

    public WorkflowTaskRepository(WorkflowDbContext context)
    {
        _context = context;
    }

    public async Task<List<WorkflowTaskInfo>> GetAll(CancellationToken cancellationToken) =>
        await _context.WorkflowTasks
            .ProjectToWorkflowTaskInfoDto()
            .ToListAsync(cancellationToken);

    public async Task CreateWorkflowTask(Manifest manifest, CancellationToken cancellationToken)
    {
        var workflowTask = new WorkflowTask(
            Name: manifest.Name,
            Path: manifest.Path,
            Version: manifest.Version);

        foreach (var channel in manifest.Channels)
        {
            DAL.Channel newChannel = new DAL.Channel(channel.Name, null, null);

            foreach (var parameter in channel.Parameters)
            {
                newChannel.Parameters.Add(new(
                    Name: parameter.Name,
                    Type: parameter.Type
                ));
            }

            if (channel.Type is ManifestChannelType.Input)
                workflowTask.InputChannels.Add(newChannel);
            else if (channel.Type is ManifestChannelType.Output)
                workflowTask.OutputChannels.Add(newChannel);
        }

        _context.Add(workflowTask);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
