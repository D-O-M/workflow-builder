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
    private const int CurrentSchemaVersion = 4;
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
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(candidate => candidate.Key == definitionKey, cancellationToken);

        var mappedTasks = model.Tasks
            .Select(task => new BlueprintNodeInputModel
            {
                InstanceId = task.TaskKey,
                PortableTaskKey = task.TaskKey,
                ConditionalRule = task.ConditionalRule,
                NodeKind = NormalizeNodeKind(task.NodeKind).ToString(),
                TaskDefinitionId = NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.PluginTask
                    ? resolvedTasks[task.TaskKey].TaskDefinitionId
                    : null,
                CollectionIteratorItemKind = NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.CollectionIterator
                    ? NormalizeCollectionIteratorItemKind(task.CollectionIteratorItemKind)
                    : null,
                AllowedMajor = NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.PluginTask ? task.AllowedMajor : 0,
                AllowedMinor = NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.PluginTask ? task.AllowedMinor : 0,
                Position = task.Position,
                DesignTimeInputOverrides = task.DesignTimeInputOverrides,
                OutgoingConnections = task.OutgoingConnections
                    .Select(connection => new BlueprintConnectionInputModel
                    {
                        ChannelName = connection.ChannelName,
                        ConnectedBlueprintNodeId = connection.ConnectedTaskKey,
                        Anchor = connection.Anchor,
                        Side = connection.Side,
                        Position = connection.Position,
                        PropertyMappings = connection.PropertyMappings
                            .Select(mapping => new BlueprintPropertyMappingInputModel
                            {
                                SourceProperty = mapping.SourceProperty,
                                TargetProperty = mapping.TargetProperty
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();

        var validationModel = new BlueprintVersionInputModel
        {
            BlueprintDefinitionId = definition?.Id ?? 1,
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
            if (!string.Equals(definition.DisplayName, displayName, StringComparison.Ordinal) &&
                !model.IgnoreImportedDisplayNameMismatch)
            {
                return new BlueprintImportViewModel
                {
                    Status = BlueprintImportStatus.DisplayNameConflict,
                    BlueprintDefinitionId = definition.Id,
                    BlueprintVersionId = 0,
                    BlueprintKey = definition.Key,
                    StoredDisplayName = definition.DisplayName,
                    ImportedDisplayName = displayName,
                    CanIgnoreImportedDisplayNameMismatch = true,
                };
            }

            var importedSignature = CreateCanonicalSignature(model);

            foreach (var existingVersion in definition.Versions.OrderByDescending(version => version.Version))
            {
                if (CreateCanonicalSignature(MapExportedBlueprint(definition, existingVersion)) == importedSignature)
                {
                    return new BlueprintImportViewModel
                    {
                        Status = BlueprintImportStatus.MatchedExistingVersion,
                        BlueprintDefinitionId = definition.Id,
                        BlueprintVersionId = existingVersion.Id,
                        BlueprintKey = definition.Key
                    };
                }
            }

            var createdVersion = await _blueprintRepository.CreateVersion(validationModel, cancellationToken);

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

        if (model.SchemaVersion is not (3 or CurrentSchemaVersion))
        {
            errors.Add(new BlueprintImportValidationError(
                BlueprintImportErrorCodes.UnsupportedSchemaVersion,
                $"Supported schema versions are 3 and {CurrentSchemaVersion}.",
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

            if (!Enum.TryParse<BlueprintNodeKind>(task.NodeKind, ignoreCase: true, out var nodeKind) ||
                !Enum.IsDefined(nodeKind))
            {
                errors.Add(new BlueprintImportValidationError(
                    BlueprintImportErrorCodes.NodeKindInvalid,
                    $"Node kind '{task.NodeKind}' is not supported.",
                    $"tasks[{index}].nodeKind"));
                continue;
            }

            if (nodeKind == BlueprintNodeKind.ConditionalBranch && model.SchemaVersion < 4)
                errors.Add(new(BlueprintImportErrorCodes.UnsupportedSchemaVersion, "Conditional Branch requires schemaVersion 4.", nameof(model.SchemaVersion)));

            if (nodeKind == BlueprintNodeKind.PluginTask && string.IsNullOrWhiteSpace(task.TaskDefinitionName))
            {
                errors.Add(new BlueprintImportValidationError(
                    BlueprintImportErrorCodes.TaskDefinitionMissing,
                    "Plugin task nodes must include taskDefinitionName.",
                    $"tasks[{index}].taskDefinitionName"));
            }

            if (nodeKind == BlueprintNodeKind.CollectionIterator &&
                !TryNormalizeCollectionIteratorItemKind(task.CollectionIteratorItemKind, out _))
            {
                errors.Add(new BlueprintImportValidationError(
                    BlueprintImportErrorCodes.CollectionIteratorItemKindInvalid,
                    "Collection Iterator item kind must be one of the supported scalar property types.",
                    $"tasks[{index}].collectionIteratorItemKind"));
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
            .Where(task => NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.PluginTask)
            .Select(task => task.TaskDefinitionName!.Trim())
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
            if (NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.ConditionalBranch)
                continue; // The graph validator checks both conditional channels and their inherited schemas.
            if (NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.CollectionIterator)
            {
                foreach (var (connection, connectionIndex) in task.OutgoingConnections.Select((connection, connectionIndex) => (connection, connectionIndex)))
                {
                    if (!string.Equals(connection.ChannelName, "Item", StringComparison.Ordinal))
                    {
                        errors.Add(new BlueprintImportValidationError(
                            BlueprintImportErrorCodes.ChannelMissing,
                            $"Channel '{connection.ChannelName}' does not exist on Collection Iterator.",
                            $"tasks[{taskIndex}].outgoingConnections[{connectionIndex}].channelName"));
                    }
                }

                continue;
            }

            if (!taskDefinitionsByName.TryGetValue(task.TaskDefinitionName!.Trim(), out var taskDefinition))
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
            .Where(task => NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.PluginTask)
            .Select(task => task.TaskDefinitionName!.Trim())
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
                if (NormalizeNodeKind(task.NodeKind) != BlueprintNodeKind.PluginTask)
                {
                    return ResolvedImportTask.Native;
                }

                var taskDefinition = taskDefinitionsByName[task.TaskDefinitionName!.Trim()];
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
        var keys = version.BlueprintNodes.ToDictionary(node => node.Id, node => node.PortableTaskKey ?? node.Id);
        return new ExportedBlueprintViewModel
        {
            SchemaVersion = version.BlueprintNodes.Any(node => node.NodeKind == BlueprintNodeKind.ConditionalBranch) ? CurrentSchemaVersion : 3,
            DefinitionKey = definition.Key,
            DisplayName = definition.DisplayName,
            StartingTaskKey = keys.GetValueOrDefault(version.StartingBlueprintNodeId ?? Guid.Empty),
            Tasks = version.BlueprintNodes
                .OrderBy(task => task.Id)
                .Select(task => new ExportedBlueprintNodeViewModel
                {
                    TaskKey = keys[task.Id],
                    ConditionalRule = task.ConditionalRuleJson is null ? null : JsonSerializer.Deserialize<ConditionalRule>(task.ConditionalRuleJson, CanonicalJsonOptions),
                    NodeKind = task.NodeKind.ToString(),
                    TaskDefinitionName = task.NodeKind == BlueprintNodeKind.PluginTask
                        ? task.TaskDefinition?.Name
                        : null,
                    CollectionIteratorItemKind = task.NodeKind == BlueprintNodeKind.CollectionIterator
                        ? NormalizeCollectionIteratorItemKind(task.CollectionIteratorItemKind)
                        : null,
                    AllowedMajor = task.NodeKind == BlueprintNodeKind.PluginTask ? task.AllowedMajor : 0,
                    AllowedMinor = task.NodeKind == BlueprintNodeKind.PluginTask ? task.AllowedMinor : 0,
                    Position = new CoordinatesViewModel
                    {
                        X = task.PositionX,
                        Y = task.PositionY
                    },
                    DesignTimeInputOverrides = DeserializeJsonMap(task.DesignTimeInputOverridesJson),
                    OutgoingConnections = task.OutgoingConnections
                        .Where(connection => connection.DestinationBlueprintNodeId.HasValue)
                        .OrderBy(connection => connection.ChannelName)
                        .ThenBy(connection => connection.DestinationBlueprintNodeId)
                        .ThenBy(connection => connection.Anchor)
                        .Select(connection => new ExportedBlueprintConnectionViewModel
                        {
                            ChannelName = connection.ChannelName,
                            ConnectedTaskKey = keys[connection.DestinationBlueprintNodeId!.Value],
                            Anchor = connection.Anchor,
                            Side = connection.Side,
                            Position = connection.Position,
                            PropertyMappings = DeserializePropertyMappings(connection.PropertyMappingsJson)
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
            StartingTaskKey = model.StartingTaskKey,
            Tasks = model.Tasks
                .Select(task => new ComparableTask
                {
                    TaskKey = task.TaskKey,
                    ConditionalRule = task.ConditionalRule,
                    NodeKind = NormalizeNodeKind(task.NodeKind).ToString(),
                    TaskDefinitionName = NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.PluginTask
                        ? task.TaskDefinitionName?.Trim()
                        : null,
                    CollectionIteratorItemKind = NormalizeNodeKind(task.NodeKind) == BlueprintNodeKind.CollectionIterator
                        ? NormalizeCollectionIteratorItemKind(task.CollectionIteratorItemKind)
                        : null,
                    AllowedMajor = task.AllowedMajor,
                    AllowedMinor = task.AllowedMinor,
                    PositionX = task.Position.X,
                    PositionY = task.Position.Y,
                    DesignTimeInputOverrides = CanonicalizeJsonMap(task.DesignTimeInputOverrides),
                    OutgoingConnections = task.OutgoingConnections
                        .Select(connection => new ComparableConnection
                        {
                            ChannelName = connection.ChannelName.Trim(),
                            ConnectedTaskKey = connection.ConnectedTaskKey,
                            Anchor = connection.Anchor,
                            Side = connection.Side,
                            Position = connection.Position,
                            PropertyMappings = connection.PropertyMappings
                                .Select(mapping => new ComparablePropertyMapping
                                {
                                    SourceProperty = mapping.SourceProperty.Trim(),
                                    TargetProperty = mapping.TargetProperty.Trim()
                                })
                                .OrderBy(mapping => mapping.TargetProperty, StringComparer.Ordinal)
                                .ThenBy(mapping => mapping.SourceProperty, StringComparer.Ordinal)
                                .ToList()
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
            StartingTaskKey = model.StartingTaskKey,
            Tasks = model.Tasks
                .Select(task => new ComparableTask
                {
                    TaskKey = task.TaskKey,
                    ConditionalRule = task.ConditionalRule,
                    NodeKind = NormalizeNodeKind(task.NodeKind).ToString(),
                    TaskDefinitionName = task.TaskDefinitionName,
                    CollectionIteratorItemKind = task.CollectionIteratorItemKind,
                    AllowedMajor = task.AllowedMajor,
                    AllowedMinor = task.AllowedMinor,
                    PositionX = task.Position.X,
                    PositionY = task.Position.Y,
                    DesignTimeInputOverrides = CanonicalizeJsonMap(task.DesignTimeInputOverrides),
                    OutgoingConnections = task.OutgoingConnections
                        .Select(connection => new ComparableConnection
                        {
                            ChannelName = connection.ChannelName,
                            ConnectedTaskKey = connection.ConnectedTaskKey,
                            Anchor = connection.Anchor,
                            Side = connection.Side,
                            Position = connection.Position,
                            PropertyMappings = connection.PropertyMappings
                                .Select(mapping => new ComparablePropertyMapping
                                {
                                    SourceProperty = mapping.SourceProperty,
                                    TargetProperty = mapping.TargetProperty
                                })
                                .OrderBy(mapping => mapping.TargetProperty, StringComparer.Ordinal)
                                .ThenBy(mapping => mapping.SourceProperty, StringComparer.Ordinal)
                                .ToList()
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

    private sealed record ResolvedImportTask(int TaskDefinitionId, TaskVersion? ResolvedVersion)
    {
        public static ResolvedImportTask Native { get; } = new(0, null);
    }

    private sealed class ComparableBlueprint
    {
        public required string DefinitionKey { get; init; }
        public required Guid StartingTaskKey { get; init; }
        public required List<ComparableTask> Tasks { get; init; }
    }

    private sealed class ComparableTask
    {
        public required Guid TaskKey { get; init; }
        public required string NodeKind { get; init; }
        public required string? TaskDefinitionName { get; init; }
        public ConditionalRule? ConditionalRule { get; init; }
        public required string? CollectionIteratorItemKind { get; init; }
        public int AllowedMajor { get; init; }
        public int AllowedMinor { get; init; }
        public int PositionX { get; init; }
        public int PositionY { get; init; }
        public required string? DesignTimeInputOverrides { get; init; }
        public required List<ComparableConnection> OutgoingConnections { get; init; }
    }

    private sealed class ComparableConnection
    {
        public required string ChannelName { get; init; }
        public required Guid ConnectedTaskKey { get; init; }
        public int Anchor { get; init; }
        public required string Side { get; init; }
        public int Position { get; init; }
        public required List<ComparablePropertyMapping> PropertyMappings { get; init; }
    }

    private sealed class ComparablePropertyMapping
    {
        public required string SourceProperty { get; init; }
        public required string TargetProperty { get; init; }
    }

    private static List<BlueprintPropertyMappingViewModel> DeserializePropertyMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<BlueprintPropertyMappingViewModel>>(json, CanonicalJsonOptions) ?? [];
    }

    private static Dictionary<string, JsonElement> DeserializeJsonMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, CanonicalJsonOptions) ?? [];
    }

    private static string? CanonicalizeJsonMap(IReadOnlyDictionary<string, JsonElement> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered, CanonicalJsonOptions);
    }

    private static BlueprintNodeKind NormalizeNodeKind(string? nodeKind) =>
        Enum.TryParse<BlueprintNodeKind>(nodeKind, ignoreCase: true, out var parsed) &&
        Enum.IsDefined(parsed)
            ? parsed
            : BlueprintNodeKind.PluginTask;

    private static bool TryNormalizeCollectionIteratorItemKind(string? itemKind, out string normalized)
    {
        normalized = WorkflowPropertyKind.String.ToString();
        if (!WorkflowPropertyTypeName.TryParse(itemKind, out var parsedKind, out var isCollection) || isCollection)
        {
            return false;
        }

        normalized = WorkflowPropertyTypeName.Format(parsedKind);
        return true;
    }

    private static string NormalizeCollectionIteratorItemKind(string? itemKind) =>
        TryNormalizeCollectionIteratorItemKind(itemKind, out var normalized)
            ? normalized
            : WorkflowPropertyKind.String.ToString();
}
