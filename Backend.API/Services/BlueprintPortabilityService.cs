using System.Text.Json;
using Backend.API.Validation.Blueprints;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Models.DomainModels;
using Backend.Models.InputModels;
using Backend.Models.ViewModels;
using Backend.Models.Versioning;
using Microsoft.EntityFrameworkCore;
using Backend.Repositories;

namespace Backend.API.Services;

public sealed class BlueprintPortabilityService
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly BlueprintDbContext _context;
    private readonly IBlueprintRepository _blueprintRepository;
    private readonly IBlueprintVersionValidator _blueprintVersionValidator;

    public BlueprintPortabilityService(
        BlueprintDbContext context,
        IBlueprintRepository blueprintRepository,
        IBlueprintVersionValidator blueprintVersionValidator)
    {
        _context = context;
        _blueprintRepository = blueprintRepository;
        _blueprintVersionValidator = blueprintVersionValidator;
    }

    public async Task<ExportedBlueprintViewModel> ExportLatestBlueprintAsync(
        int definitionId,
        CancellationToken cancellationToken)
    {
        var definition = await _context.BlueprintDefinitions
            .AsNoTracking()
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.BlueprintNodes)
                    .ThenInclude(taskInstance => taskInstance.TaskDefinition)
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.BlueprintNodes)
                    .ThenInclude(taskInstance => taskInstance.OutgoingConnections)
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Id == definitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(definitionId);
        }

        var latestVersion = definition.Versions
            .OrderByDescending(version => version.Version)
            .FirstOrDefault();

        if (latestVersion is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(definitionId);
        }

        return MapExportedBlueprint(definition, latestVersion);
    }

    public async Task<BlueprintImportViewModel> ImportBlueprintAsync(
        ExportedBlueprintInputModel model,
        CancellationToken cancellationToken)
    {
        var definitionKey = model.DefinitionKey.Trim();
        var displayName = model.DisplayName.Trim();
        var versionName = model.VersionName.Trim();

        var importErrors = await ValidateImportAsync(model, cancellationToken);
        if (importErrors.Count > 0)
        {
            throw new BlueprintImportValidationException(importErrors);
        }

        var resolvedTasks = await ResolveImportedTasksAsync(model, cancellationToken);

        var definition = await _context.BlueprintDefinitions
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.BlueprintNodes)
                    .ThenInclude(taskInstance => taskInstance.TaskDefinition)
            .Include(candidate => candidate.Versions)
                .ThenInclude(version => version.BlueprintNodes)
                    .ThenInclude(taskInstance => taskInstance.OutgoingConnections)
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Key == definitionKey, cancellationToken);

        var mappedTasks = model.Tasks
            .Select(task => new BlueprintNodeInputModel
            {
                InstanceId = task.TaskKey,
                TaskDefinitionId = resolvedTasks[task.TaskKey].TaskDefinitionId,
                AllowedMajor = task.AllowedMajor,
                AllowedMinor = task.AllowedMinor,
                Position = task.Position,
                OutgoingConnections = task.OutgoingConnections
                    .Select(connection => new BlueprintConnectionInputModel
                    {
                        ChannelName = connection.ChannelName,
                        ConnectedBlueprintNodeId = connection.ConnectedTaskKey,
                        Anchor = connection.Anchor,
                        Side = connection.Side,
                        Position = connection.Position
                    })
                    .ToList()
            })
            .ToList();

        var validationModel = new BlueprintVersionInputModel
        {
            BlueprintDefinitionId = definition?.Id ?? 1,
            Name = versionName,
            StartingBlueprintNodeId = model.StartingTaskKey,
            Tasks = mappedTasks
        };

        var validation = await _blueprintVersionValidator.ValidateAsync(validationModel, cancellationToken);
        if (!validation.IsValid)
        {
            throw new BlueprintImportValidationException(validation.Errors
                .Select(error => new BlueprintImportValidationError(error.Code, error.Message, error.Path)));
        }

        if (definition is not null)
        {
            definition.DisplayName = displayName;
            var importedSignature = CreateCanonicalSignature(model);

            foreach (var existingVersion in definition.Versions.OrderByDescending(version => version.Version))
            {
                if (CreateCanonicalSignature(MapExportedBlueprint(definition, existingVersion)) == importedSignature)
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    return new BlueprintImportViewModel
                    {
                        Status = BlueprintImportStatus.MatchedExistingVersion,
                        BlueprintDefinitionId = definition.Id,
                        BlueprintVersionId = existingVersion.Id,
                        BlueprintKey = definition.Key
                    };
                }
            }

            var createdVersion = await _blueprintRepository.CreateVersion(
                BuildImportedVersion(definition.Id, validationModel),
                cancellationToken);

            return new BlueprintImportViewModel
            {
                Status = BlueprintImportStatus.CreatedVersion,
                BlueprintDefinitionId = definition.Id,
                BlueprintVersionId = createdVersion.BlueprintVersionId,
                BlueprintKey = definition.Key
            };
        }

        var createdBlueprint = await _blueprintRepository.CreateBlueprint(
            new BlueprintInputModel
            {
                Key = definitionKey,
                Name = displayName,
                VersionName = versionName,
                StartingBlueprintNodeId = model.StartingTaskKey,
                Tasks = mappedTasks
            },
            cancellationToken);

        var createdVersionId = createdBlueprint.Versions
            .OrderByDescending(version => version.Version)
            .Select(version => version.BlueprintVersionId)
            .FirstOrDefault();

        return new BlueprintImportViewModel
        {
            Status = BlueprintImportStatus.CreatedDefinition,
            BlueprintDefinitionId = createdBlueprint.Id,
            BlueprintVersionId = createdVersionId,
            BlueprintKey = createdBlueprint.Key
        };
    }

    private async Task<List<BlueprintImportValidationError>> ValidateImportAsync(
        ExportedBlueprintInputModel model,
        CancellationToken cancellationToken)
    {
        var errors = new List<BlueprintImportValidationError>();

        if (model.SchemaVersion != 1)
        {
            errors.Add(new BlueprintImportValidationError(
                BlueprintImportErrorCodes.UnsupportedSchemaVersion,
                "Only schemaVersion 1 is supported.",
                nameof(model.SchemaVersion)));
        }

        if (string.IsNullOrWhiteSpace(model.DefinitionKey))
        {
            errors.Add(new BlueprintImportValidationError(
                BlueprintImportErrorCodes.DefinitionKeyMissing,
                "definitionKey is required.",
                nameof(model.DefinitionKey)));
        }

        if (string.IsNullOrWhiteSpace(model.DisplayName))
        {
            errors.Add(new BlueprintImportValidationError(
                BlueprintImportErrorCodes.DisplayNameMissing,
                "displayName is required.",
                nameof(model.DisplayName)));
        }

        if (string.IsNullOrWhiteSpace(model.VersionName))
        {
            errors.Add(new BlueprintImportValidationError(
                BlueprintImportErrorCodes.VersionNameMissing,
                "versionName is required.",
                nameof(model.VersionName)));
        }

        var tasksByKey = new Dictionary<Guid, ExportedBlueprintNodeInputModel>();
        for (var index = 0; index < model.Tasks.Count; index++)
        {
            var task = model.Tasks[index];
            if (!tasksByKey.TryAdd(task.TaskKey, task))
            {
                errors.Add(new BlueprintImportValidationError(
                    BlueprintImportErrorCodes.TaskKeyDuplicate,
                    "taskKey values must be unique.",
                    $"tasks[{index}].taskKey"));
            }
        }

        if (model.StartingTaskKey == Guid.Empty || !tasksByKey.ContainsKey(model.StartingTaskKey))
        {
            errors.Add(new BlueprintImportValidationError(
                BlueprintImportErrorCodes.StartingTaskMissing,
                "startingTaskKey must reference a task in tasks[].",
                nameof(model.StartingTaskKey)));
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        var taskDefinitionNames = model.Tasks
            .Select(task => task.TaskDefinitionName.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var taskDefinitions = await _context.TaskDefinitions
            .AsNoTracking()
            .Include(taskDefinition => taskDefinition.Versions)
                .ThenInclude(taskVersion => taskVersion.Channels)
            .Where(taskDefinition => taskDefinitionNames.Contains(taskDefinition.Name))
            .ToListAsync(cancellationToken);

        var taskDefinitionsByName = taskDefinitions.ToDictionary(taskDefinition => taskDefinition.Name, StringComparer.Ordinal);

        foreach (var (task, taskIndex) in model.Tasks.Select((task, taskIndex) => (task, taskIndex)))
        {
            if (!taskDefinitionsByName.TryGetValue(task.TaskDefinitionName.Trim(), out var taskDefinition))
            {
                errors.Add(new BlueprintImportValidationError(
                    BlueprintImportErrorCodes.TaskDefinitionMissing,
                    $"Task definition '{task.TaskDefinitionName}' does not exist.",
                    $"tasks[{taskIndex}].taskDefinitionName"));
                continue;
            }

            var resolvedVersion = ResolveLaneVersion(taskDefinition, task.AllowedMajor, task.AllowedMinor);
            if (resolvedVersion is null)
            {
                errors.Add(new BlueprintImportValidationError(
                    BlueprintImportErrorCodes.TaskLaneMissing,
                    $"Task definition '{task.TaskDefinitionName}' does not have lane {task.AllowedMajor}.{task.AllowedMinor}.",
                    $"tasks[{taskIndex}]"));
                continue;
            }

            var outputChannelNames = resolvedVersion.OutputChannels
                .Select(channel => channel.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var (connection, connectionIndex) in task.OutgoingConnections.Select((connection, connectionIndex) => (connection, connectionIndex)))
            {
                if (!outputChannelNames.Contains(connection.ChannelName))
                {
                    errors.Add(new BlueprintImportValidationError(
                        BlueprintImportErrorCodes.ChannelMissing,
                        $"Channel '{connection.ChannelName}' does not exist on task definition '{task.TaskDefinitionName}' lane {task.AllowedMajor}.{task.AllowedMinor}.",
                        $"tasks[{taskIndex}].outgoingConnections[{connectionIndex}].channelName"));
                }
            }
        }

        return errors;
    }

    private async Task<Dictionary<Guid, ResolvedImportTask>> ResolveImportedTasksAsync(
        ExportedBlueprintInputModel model,
        CancellationToken cancellationToken)
    {
        var taskDefinitionNames = model.Tasks
            .Select(task => task.TaskDefinitionName.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var taskDefinitions = await _context.TaskDefinitions
            .AsNoTracking()
            .Include(taskDefinition => taskDefinition.Versions)
                .ThenInclude(taskVersion => taskVersion.Channels)
            .Where(taskDefinition => taskDefinitionNames.Contains(taskDefinition.Name))
            .ToListAsync(cancellationToken);

        var taskDefinitionsByName = taskDefinitions.ToDictionary(taskDefinition => taskDefinition.Name, StringComparer.Ordinal);

        return model.Tasks.ToDictionary(
            task => task.TaskKey,
            task =>
            {
                var taskDefinition = taskDefinitionsByName[task.TaskDefinitionName.Trim()];
                var resolvedVersion = ResolveLaneVersion(taskDefinition, task.AllowedMajor, task.AllowedMinor)!;
                return new ResolvedImportTask(taskDefinition.Id, resolvedVersion);
            });
    }

    private static TaskVersion? ResolveLaneVersion(TaskDefinition taskDefinition, int allowedMajor, int allowedMinor) =>
        taskDefinition.Versions
            .Where(version => SemanticVersionUtility.IsSameLane(version.Version, allowedMajor, allowedMinor))
            .OrderByDescending(version => version.Version)
            .FirstOrDefault();

    private static ExportedBlueprintViewModel MapExportedBlueprint(
        BlueprintDefinition definition,
        BlueprintVersion version)
    {
        return new ExportedBlueprintViewModel
        {
            SchemaVersion = 1,
            DefinitionKey = definition.Key,
            DisplayName = definition.DisplayName,
            VersionName = version.Name,
            StartingTaskKey = version.StartingBlueprintNodeId ?? Guid.Empty,
            Tasks = version.BlueprintNodes
                .OrderBy(task => task.Id)
                .Select(task => new ExportedBlueprintNodeViewModel
                {
                    TaskKey = task.Id,
                    TaskDefinitionName = task.TaskDefinition.Name,
                    AllowedMajor = task.AllowedMajor,
                    AllowedMinor = task.AllowedMinor,
                    Position = new CoordinatesViewModel
                    {
                        X = task.PositionX,
                        Y = task.PositionY
                    },
                    OutgoingConnections = task.OutgoingConnections
                        .Where(connection => connection.DestinationBlueprintNodeId.HasValue)
                        .OrderBy(connection => connection.ChannelName)
                        .ThenBy(connection => connection.DestinationBlueprintNodeId)
                        .ThenBy(connection => connection.Anchor)
                        .Select(connection => new ExportedBlueprintConnectionViewModel
                        {
                            ChannelName = connection.ChannelName,
                            ConnectedTaskKey = connection.DestinationBlueprintNodeId!.Value,
                            Anchor = connection.Anchor,
                            Side = connection.Side,
                            Position = connection.Position
                        })
                        .ToList()
                })
                .ToList()
        };
    }

    private static string CreateCanonicalSignature(ExportedBlueprintInputModel model)
    {
        var comparable = new ComparableBlueprint
        {
            DefinitionKey = model.DefinitionKey.Trim(),
            VersionName = model.VersionName.Trim(),
            StartingTaskKey = model.StartingTaskKey,
            Tasks = model.Tasks
                .Select(task => new ComparableTask
                {
                    TaskKey = task.TaskKey,
                    TaskDefinitionName = task.TaskDefinitionName.Trim(),
                    AllowedMajor = task.AllowedMajor,
                    AllowedMinor = task.AllowedMinor,
                    PositionX = task.Position.X,
                    PositionY = task.Position.Y,
                    OutgoingConnections = task.OutgoingConnections
                        .Select(connection => new ComparableConnection
                        {
                            ChannelName = connection.ChannelName.Trim(),
                            ConnectedTaskKey = connection.ConnectedTaskKey,
                            Anchor = connection.Anchor,
                            Side = connection.Side,
                            Position = connection.Position
                        })
                        .OrderBy(connection => connection.ChannelName, StringComparer.Ordinal)
                        .ThenBy(connection => connection.ConnectedTaskKey)
                        .ThenBy(connection => connection.Anchor)
                        .ThenBy(connection => connection.Side, StringComparer.Ordinal)
                        .ThenBy(connection => connection.Position)
                        .ToList()
                })
                .OrderBy(task => task.TaskKey)
                .ToList()
        };

        return JsonSerializer.Serialize(comparable, CanonicalJsonOptions);
    }

    private static string CreateCanonicalSignature(ExportedBlueprintViewModel model)
    {
        var comparable = new ComparableBlueprint
        {
            DefinitionKey = model.DefinitionKey,
            VersionName = model.VersionName,
            StartingTaskKey = model.StartingTaskKey,
            Tasks = model.Tasks
                .Select(task => new ComparableTask
                {
                    TaskKey = task.TaskKey,
                    TaskDefinitionName = task.TaskDefinitionName,
                    AllowedMajor = task.AllowedMajor,
                    AllowedMinor = task.AllowedMinor,
                    PositionX = task.Position.X,
                    PositionY = task.Position.Y,
                    OutgoingConnections = task.OutgoingConnections
                        .Select(connection => new ComparableConnection
                        {
                            ChannelName = connection.ChannelName,
                            ConnectedTaskKey = connection.ConnectedTaskKey,
                            Anchor = connection.Anchor,
                            Side = connection.Side,
                            Position = connection.Position
                        })
                        .OrderBy(connection => connection.ChannelName, StringComparer.Ordinal)
                        .ThenBy(connection => connection.ConnectedTaskKey)
                        .ThenBy(connection => connection.Anchor)
                        .ThenBy(connection => connection.Side, StringComparer.Ordinal)
                        .ThenBy(connection => connection.Position)
                        .ToList()
                })
                .OrderBy(task => task.TaskKey)
                .ToList()
        };

        return JsonSerializer.Serialize(comparable, CanonicalJsonOptions);
    }

    private static FullBlueprintVersion BuildImportedVersion(
        int blueprintDefinitionId,
        BlueprintVersionInputModel model)
    {
        var taskModelsByInstanceId = model.Tasks.ToDictionary(task => task.InstanceId, task => task);
        return new FullBlueprintVersion
        {
            DefinitionId = blueprintDefinitionId,
            Name = model.Name,
            TaskTree = BuildTaskTree(taskModelsByInstanceId[model.StartingBlueprintNodeId], taskModelsByInstanceId)
        };
    }

    private static BlueprintTaskTree BuildTaskTree(
        BlueprintNodeInputModel model,
        IReadOnlyDictionary<Guid, BlueprintNodeInputModel> taskModelsByInstanceId)
    {
        var task = new BlueprintTaskTree
        {
            InstanceId = model.InstanceId,
            TaskDefinitionId = model.TaskDefinitionId,
            AllowedMajor = model.AllowedMajor,
            AllowedMinor = model.AllowedMinor,
            Position = new Coordinates(model.Position.X, model.Position.Y)
        };

        foreach (var connection in model.OutgoingConnections)
        {
            task.OutgoingConnections.Add(new Backend.Models.DomainModels.BlueprintConnection
            {
                ChannelDetails = new ChannelDetails
                {
                    ChannelId = connection.ChannelId,
                    ChannelName = connection.ChannelName
                },
                Anchor = connection.Anchor,
                Side = connection.Side,
                Position = connection.Position,
                Task = BuildTaskTree(taskModelsByInstanceId[connection.ConnectedBlueprintNodeId], taskModelsByInstanceId)
            });
        }

        return task;
    }

    private sealed record ResolvedImportTask(int TaskDefinitionId, TaskVersion ResolvedVersion);

    private sealed class ComparableBlueprint
    {
        public required string DefinitionKey { get; init; }
        public required string VersionName { get; init; }
        public required Guid StartingTaskKey { get; init; }
        public required List<ComparableTask> Tasks { get; init; }
    }

    private sealed class ComparableTask
    {
        public required Guid TaskKey { get; init; }
        public required string TaskDefinitionName { get; init; }
        public int AllowedMajor { get; init; }
        public int AllowedMinor { get; init; }
        public int PositionX { get; init; }
        public int PositionY { get; init; }
        public required List<ComparableConnection> OutgoingConnections { get; init; }
    }

    private sealed class ComparableConnection
    {
        public required string ChannelName { get; init; }
        public required Guid ConnectedTaskKey { get; init; }
        public int Anchor { get; init; }
        public required string Side { get; init; }
        public int Position { get; init; }
    }
}
