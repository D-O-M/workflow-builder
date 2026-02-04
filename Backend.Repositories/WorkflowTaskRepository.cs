using Backend.Database.Relational.Entities;
using Backend.DTOs;
using Backend.Interfaces.Repositories;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore;

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
            .Select(ts => new WorkflowTaskInfo(ts.Id, ts.Name, ts.Path, ts.Version))
            .ToListAsync(cancellationToken);

    public async Task CreateTaskSchema(Manifest manifest, CancellationToken cancellationToken)
    {
        ChannelSpecification? inputChannelSpec = null;
        var outputChannelSpecs = new List<ChannelSpecification>();

        foreach (var channel in manifest.Channels)
        {
            var newChannel = new ChannelSpecification
            {
                Name = channel.Name
            };

            foreach (var parameter in channel.Parameters)
            {
                newChannel.Properties.Add(new()
                {
                    Name = parameter.Name,
                    Type = parameter.Type
                });
            }

            if (channel.ChannelType is ManifestChannelType.Input)
            {
                if (inputChannelSpec is not null)
                    throw new InvalidDataException("Multiple input channels are not supported.");

                inputChannelSpec = newChannel;
            }
            else if (channel.ChannelType is ManifestChannelType.Output)
                outputChannelSpecs.Add(newChannel);
        }

        if (inputChannelSpec is null)
            throw new InvalidDataException("An input channel is required.");

        var workflowTask = new TaskSchema
        {
            Name = manifest.Name,
            Path = manifest.Path,
            Version = manifest.Version,
            InputChannelSpecification = inputChannelSpec!,
            OutputChannelSpecifications = outputChannelSpecs
        };

        _context.Add(workflowTask);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task CreateBasicTask(string name, string version,
        string entryPath, CancellationToken cancellationToken)
    {
        var taskSchema = new TaskSchema
        {
            Name = name,
            Path = entryPath,
            Version = new Version(version),
            OutputChannelSpecifications = []
        };

        _context.Add(taskSchema);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
