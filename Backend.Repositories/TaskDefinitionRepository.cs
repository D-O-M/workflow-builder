using System.Text.Json;
using Backend.Database.Relational.Entities;
using Backend.Models;
using Backend.Interfaces.Repositories;
using Backend.Database.Relational;
using Microsoft.EntityFrameworkCore;
using Backend.Models.DomainModels;
using Backend.Models.ViewModels;
using Backend.Models.Mappers;

namespace Backend.Repositories;

public class TaskDefinitionRepository : ITaskDefinitionRepository
{
    private readonly BlueprintDbContext _context;
    private readonly TaskViewModelMapper _mapper = new();

    public TaskDefinitionRepository(BlueprintDbContext context)
    {
        _context = context;
    }

    public async Task<List<TaskDefinitionInfo>> GetAll(CancellationToken cancellationToken) =>
        await _context.TaskVersions
            .Select(ts => new TaskDefinitionInfo(
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
            .ToListAsync(cancellationToken))
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
                DisplayName = NormalizeDisplayName(version.DisplayName, version.Name),
                Versions = []
            };

            _context.Add(taskDefinitionEntity);
        }
        else
        {
            taskDefinitionEntity.Versions ??= [];
            taskDefinitionEntity.DisplayName = NormalizeDisplayName(
                version.DisplayName,
                taskDefinitionEntity.DisplayName ?? taskDefinitionEntity.Name);
        }

        ArgumentNullException.ThrowIfNull(taskDefinitionEntity);
        var changeKind = DetermineChangeKind(taskDefinitionEntity.Versions, version.Version);
        var existingVersionEntity = taskDefinitionEntity.Versions.FirstOrDefault(candidate =>
            candidate.Version.Equals(version.Version));
        if (existingVersionEntity is not null)
        {
            await _context.SaveChangesAsync(cancellationToken);

            return new TaskVersionRegistrationResult(
                existingVersionEntity.Id,
                taskDefinitionEntity.Id,
                taskDefinitionEntity.Name,
                existingVersionEntity.Version,
                changeKind);
        }

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
                Properties = version.RequiredGlobalState.Select(MapProperty).ToList()
            };
        }

        if (version.InputChannelDefinition is not null)
        {
            versionEntity.Channels.Add(new InputChannelDefinition
            {
                Name = version.InputChannelDefinition.Name,
                Properties = version.InputChannelDefinition.Properties.Select(MapProperty).ToList()
            });
        }

        if (version.OutputChannelDefinitions.Count > 0)
        {
            foreach (var outputChannel in version.OutputChannelDefinitions)
            {
                versionEntity.Channels.Add(new OutputChannelDefinition
                {
                    Name = outputChannel.Name,
                    Properties = outputChannel.Properties.Select(MapProperty).ToList()
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

    private static string NormalizeDisplayName(string? displayName, string fallbackName) =>
        string.IsNullOrWhiteSpace(displayName)
            ? fallbackName
            : displayName.Trim();

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
            DisplayName = name,
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

    private static ChannelProperty MapProperty(PropertyDefinitionDomainModel property) =>
        new()
        {
            Name = property.Name,
            Kind = property.Kind,
            IsCollection = property.IsCollection,
            IsRequired = property.IsRequired,
            EnumValuesJson = property.EnumValues.Count > 0
                ? JsonSerializer.Serialize(property.EnumValues)
                : null
        };
}
