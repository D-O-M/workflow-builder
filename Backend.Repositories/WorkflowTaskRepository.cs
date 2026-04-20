using Backend.Database.Relational.Entities;
using Backend.Models;
using Backend.Interfaces.Repositories;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore;
using Backend.Models.DomainModels;
using Backend.Models.ViewModels;
using Backend.Models.Mappers;

namespace Backend.Repositories;

public class WorkflowTaskRepository : IWorkflowTaskRepository
{
    private readonly WorkflowDbContext _context;
    private readonly TaskViewModelMapper _mapper = new();

    public WorkflowTaskRepository(WorkflowDbContext context)
    {
        _context = context;
    }

    public async Task<List<WorkflowTaskInfo>> GetAll(CancellationToken cancellationToken) =>
        await _context.TaskVersions
            .Select(ts => new WorkflowTaskInfo(
                ts.Id,
                ts.TaskDefinitionId,
                ts.TaskDefinition!.Name,
                ts.Path,
                ts.Version,
                ts.State))
            .ToListAsync(cancellationToken);

    public async Task<List<TaskDefinitionViewModel>> GetAllTaskDefinitionViewModels(CancellationToken cancellationToken) =>
        [..(await _context.TaskDefinitions
            .Include(td => td.Versions)
                .ThenInclude(tv => tv.Channels)
                    .ThenInclude(c => c.Properties)
            .AsNoTracking()
            .ToListAsync())
            .Select(_mapper.MapViewModel)];

    public async Task<TaskVersionRegistrationResult> CreateVersion(TaskVersionDomainModel version, CancellationToken cancellationToken)
    {
        var taskDefinitionEntity = await _context.TaskDefinitions
            .Include(td => td.Versions)
            .FirstOrDefaultAsync(td => td.Name == version.Name, cancellationToken);

        if (taskDefinitionEntity is null)
        {
            taskDefinitionEntity = new TaskDefinition
            {
                Name = version.Name,
                Versions = []
            };

            _context.Add(taskDefinitionEntity);
        }
        else
        {
            taskDefinitionEntity.Versions ??= [];
        }

        ArgumentNullException.ThrowIfNull(taskDefinitionEntity);
        var changeKind = DetermineChangeKind(taskDefinitionEntity.Versions, version.Version);

        var versionEntity = new TaskVersion
        {
            Description = version.Description,
            Path = version.WorkspaceEntryPointPath,
            Version = version.Version,
            State = TaskVersionState.Registered
        };

        if (version.RequiredGlobalState is not null)
        {
            versionEntity.GlobalState = new GlobalStateDefinition
            {
                Name = "GlobalState",
                Properties = version.RequiredGlobalState.Select(p => new ChannelProperty
                {
                    Name = p.Name,
                    Type = p.TypeName switch
                    {
                        "string" => PrimitiveTypes.String,
                        "integer" or "number" => PrimitiveTypes.Number,
                        "boolean" => PrimitiveTypes.Boolean,
                        "guid" => PrimitiveTypes.Guid,
                        _ => throw new InvalidDataException($"Unsupported property type: {p.TypeName}")
                    }
                }).ToList()
            };
        }

        if (version.InputChannelDefinition is not null)
        {
            versionEntity.Channels.Add(new InputChannelDefinition
            {
                Name = version.InputChannelDefinition.Name,
                Properties = version.InputChannelDefinition.Properties.Select(p => new ChannelProperty
                {
                    Name = p.Name,
                    Type = p.TypeName switch
                    {
                        "string" => PrimitiveTypes.String,
                        "integer" or "number" => PrimitiveTypes.Number,
                        "boolean" => PrimitiveTypes.Boolean,
                        "guid" => PrimitiveTypes.Guid,
                        _ => throw new InvalidDataException($"Unsupported property type: {p.TypeName}")
                    }
                }).ToList()
            });
        }

        if (version.OutputChannelDefinitions.Count > 0)
        {
            foreach (var outputChannel in version.OutputChannelDefinitions)
            {
                versionEntity.Channels.Add(new OutputChannelDefinition
                {
                    Name = outputChannel.Name,
                    Properties = outputChannel.Properties.Select(p => new ChannelProperty
                    {
                        Name = p.Name,
                        Type = p.TypeName switch
                        {
                            "string" => PrimitiveTypes.String,
                            "integer" or "number" => PrimitiveTypes.Number,
                            "boolean" => PrimitiveTypes.Boolean,
                            "guid" => PrimitiveTypes.Guid,
                            _ => throw new InvalidDataException($"Unsupported property type: {p.TypeName}")
                        }
                    }).ToList()
                });
            }
        }

        taskDefinitionEntity.Versions.Add(versionEntity);

        await _context.SaveChangesAsync(cancellationToken);

        return new TaskVersionRegistrationResult(
            versionEntity.Id,
            taskDefinitionEntity.Id,
            taskDefinitionEntity.Name,
            versionEntity.Version,
            changeKind);
    }
    public async System.Threading.Tasks.Task CreateTaskSchema(Manifest manifest, CancellationToken cancellationToken)
    {
        /*Channel? inputChannelSpec = null;
        var outputChannelSpecs = new List<Channel>();

        foreach (var channel in manifest.Channels)
        {
            var newChannel = new Channel
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
        await _context.SaveChangesAsync(cancellationToken);*/
        throw new NotImplementedException();
    }

    public async System.Threading.Tasks.Task CreateBasicTask(string name, string version,
        string entryPath, CancellationToken cancellationToken)
    {
        var taskDefinition = await _context.TaskDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(td => td.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));

        taskDefinition ??= new TaskDefinition
        {
            Name = name,
            Versions = []
        };

        taskDefinition.Versions ??= [];

        taskDefinition.Versions.Add(new TaskVersion
        {
            Path = entryPath,
            Version = new Version(version)
        });

        _context.Add(taskDefinition);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static PluginVersionChangeKind DetermineChangeKind(
        IEnumerable<TaskVersion> existingVersions,
        Version newVersion)
    {
        var versions = existingVersions.ToList();
        if (versions.Count == 0)
        {
            return PluginVersionChangeKind.Initial;
        }

        if (versions.Any(version => version.Version.Major == newVersion.Major && version.Version.Minor == newVersion.Minor))
        {
            return PluginVersionChangeKind.Patch;
        }

        if (versions.Any(version => version.Version.Major == newVersion.Major))
        {
            return PluginVersionChangeKind.Minor;
        }

        return PluginVersionChangeKind.Major;
    }
}
