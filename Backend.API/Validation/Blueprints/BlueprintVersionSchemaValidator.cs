using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Models;
using Backend.Models.InputModels;
using Backend.Models.Versioning;
using Microsoft.EntityFrameworkCore;

namespace Backend.API.Validation.Blueprints;

public sealed class BlueprintVersionSchemaValidator : IBlueprintVersionValidator
{
    private readonly BlueprintDbContext _context;

    public BlueprintVersionSchemaValidator(BlueprintDbContext context)
    {
        _context = context;
    }

    public async Task<BlueprintValidationResult> ValidateAsync(
        BlueprintVersionInputModel model,
        CancellationToken ct = default)
    {
        try
        {
            return await ValidateGraphAsync(model, ct);
        }
        catch (InvalidDataException error)
        {
            var invalid = new BlueprintValidationResult();
            invalid.Add("graph.route.invalid", error.Message, "tasks");
            return invalid;
        }
    }

    private async Task<BlueprintValidationResult> ValidateGraphAsync(BlueprintVersionInputModel model, CancellationToken ct)
    {
        var result = new BlueprintValidationResult();
        if (model.Tasks.Count == 0)
        {
            return result;
        }

        var nodeIndex = model.Tasks
            .Select((task, index) => new { task.InstanceId, index })
            .ToDictionary(item => item.InstanceId, item => item.index);
        var taskDefinitionIds = model.Tasks
            .Where(task => ParseNodeKind(task.NodeKind) == BlueprintNodeKind.PluginTask && task.TaskDefinitionId.HasValue)
            .Select(task => task.TaskDefinitionId!.Value)
            .Distinct()
            .ToList();

        var taskDefinitions = await _context.TaskDefinitions
            .AsNoTracking()
            .Include(taskDefinition => taskDefinition.Versions)
                .ThenInclude(taskVersion => taskVersion.Channels)
                    .ThenInclude(channel => channel.Properties)
            .Where(taskDefinition => taskDefinitionIds.Contains(taskDefinition.Id))
            .ToListAsync(ct);

        var taskDefinitionsById = taskDefinitions.ToDictionary(taskDefinition => taskDefinition.Id);
        var inputPropertiesByNodeId = new Dictionary<Guid, Dictionary<string, string>>();
        var outputPropertiesByNodeId = new Dictionary<Guid, Dictionary<string, Dictionary<string, string>>>();

        var hasBranches = model.Tasks.Any(task => ParseNodeKind(task.NodeKind) == BlueprintNodeKind.ConditionalBranch);
        var branchSchemas = new BlueprintBranchSchema(model.Tasks, taskDefinitionsById);
        var requiredInputs = new Dictionary<Guid, List<string>>();

        foreach (var task in model.Tasks)
        {
            if (ParseNodeKind(task.NodeKind) == BlueprintNodeKind.ConditionalBranch)
            {
                var path = $"tasks[{nodeIndex[task.InstanceId]}]";
                if (task.TaskDefinitionId.HasValue || task.DesignTimeInputOverrides.Count > 0 || task.InstanceId == model.StartingBlueprintNodeId)
                    result.Add("task.conditionalBranch.input", "Conditional Branch requires an upstream channel and cannot have a Plugin or input overrides.", path);
                try
                {
                    var fields = branchSchemas.Resolve(task.InstanceId);
                    var properties = fields.ToDictionary(field => field.Name, field => field.Kind, StringComparer.Ordinal);
                    inputPropertiesByNodeId[task.InstanceId] = properties;
                    outputPropertiesByNodeId[task.InstanceId] = new(StringComparer.Ordinal) { ["True"] = properties, ["False"] = properties };
                    foreach (var issue in ConditionalRuleEngine.Validate(task.ConditionalRule, fields))
                        result.Add("task.conditionalBranch.rule", issue.Message, $"{path}.conditionalRule{issue.Path[4..]}");
                }
                catch (InvalidDataException error)
                {
                    result.Add("task.conditionalBranch.input", error.Message, path);
                }
                continue;
            }
            if (task.ConditionalRule is not null)
                result.Add("task.conditionalBranch.kind", "Only Conditional Branch can define a rule.", $"tasks[{nodeIndex[task.InstanceId]}].conditionalRule");
            if (ParseNodeKind(task.NodeKind) == BlueprintNodeKind.CollectionIterator)
            {
                if (!TryNormalizeCollectionIteratorItemKind(task.CollectionIteratorItemKind, out var itemKind))
                {
                    result.Add(
                        "task.collectionIterator.itemKind.invalid",
                        "Collection Iterator item kind must be one of the supported scalar property types.",
                        $"tasks[{nodeIndex[task.InstanceId]}].collectionIteratorItemKind");
                    continue;
                }

                requiredInputs[task.InstanceId] = ["items"];
                inputPropertiesByNodeId[task.InstanceId] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["items"] = WorkflowPropertyTypeName.ToCollectionKindName(itemKind)
                };
                outputPropertiesByNodeId[task.InstanceId] = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
                {
                    ["Item"] = new(StringComparer.Ordinal)
                    {
                        ["item"] = itemKind
                    }
                };
                continue;
            }

            if (!task.TaskDefinitionId.HasValue)
            {
                result.Add(
                    "task.definition.required",
                    "Plugin task nodes must reference a task definition.",
                    $"tasks[{nodeIndex[task.InstanceId]}].taskDefinitionId");
                continue;
            }

            if (!taskDefinitionsById.TryGetValue(task.TaskDefinitionId.Value, out var taskDefinition))
            {
                result.Add(
                    "task.definition.missing",
                    $"Task definition {task.TaskDefinitionId} does not exist.",
                    $"tasks[{nodeIndex[task.InstanceId]}].taskDefinitionId");
                continue;
            }

            var resolvedVersion = taskDefinition.Versions
                .Where(version => SemanticVersionUtility.IsSameLane(version.Version, task.AllowedMajor, task.AllowedMinor))
                .OrderByDescending(version => version.Version)
                .FirstOrDefault();

            if (resolvedVersion is null)
            {
                result.Add(
                    "task.lane.missing",
                    $"Task definition {task.TaskDefinitionId} does not have lane {task.AllowedMajor}.{task.AllowedMinor}.",
                    $"tasks[{nodeIndex[task.InstanceId]}]");
                continue;
            }

            var inputChannel = resolvedVersion.InputChannels.SingleOrDefault();
            if (inputChannel is null)
            {
                result.Add(
                    "task.input.missing",
                    $"Task definition {task.TaskDefinitionId} does not expose an input channel.",
                    $"tasks[{nodeIndex[task.InstanceId]}]");
                continue;
            }

            requiredInputs[task.InstanceId] = inputChannel.Properties.Where(p => p.IsRequired).Select(p => p.Name).ToList();
            inputPropertiesByNodeId[task.InstanceId] = inputChannel.Properties.ToDictionary(
                property => property.Name,
                property => WorkflowPropertyTypeName.Format(property.Kind, property.IsCollection),
                StringComparer.Ordinal);
            outputPropertiesByNodeId[task.InstanceId] = resolvedVersion.OutputChannels.ToDictionary(
                channel => channel.Name,
                channel => channel.Properties.ToDictionary(
                    property => property.Name,
                    property => WorkflowPropertyTypeName.Format(property.Kind, property.IsCollection),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

            foreach (var overrideProperty in task.DesignTimeInputOverrides.Keys)
            {
                if (!inputPropertiesByNodeId[task.InstanceId].ContainsKey(overrideProperty))
                {
                    result.Add(
                        "task.override.targetMissing",
                        $"Design-time override targets unknown input property '{overrideProperty}'.",
                        $"tasks[{nodeIndex[task.InstanceId]}].designTimeInputOverrides.{overrideProperty}");
                }
            }
        }

        if (!result.IsValid)
        {
            return result;
        }

        var routes = hasBranches ? BlueprintRouteAvailability.From(model.Tasks) : null;
        var coverage = new Dictionary<(Guid Target, string Property), int>();
        foreach (var task in model.Tasks)
        {
            var sourceOutputs = outputPropertiesByNodeId[task.InstanceId];

            foreach (var (connection, connectionIndex) in task.OutgoingConnections.Select((connection, connectionIndex) => (connection, connectionIndex)))
            {
                if (!sourceOutputs.TryGetValue(connection.ChannelName ?? string.Empty, out var sourceProperties))
                {
                    result.Add(
                        "graph.connection.channelMissing",
                        $"Channel '{connection.ChannelName}' does not exist on task {task.TaskDefinitionId}.",
                        $"tasks[{nodeIndex[task.InstanceId]}].outgoingConnections[{connectionIndex}].channelName");
                    continue;
                }

                if (!inputPropertiesByNodeId.TryGetValue(connection.ConnectedBlueprintNodeId, out var targetProperties))
                {
                    continue;
                }

                var effectiveMappings = connection.PropertyMappings.Count > 0
                    ? connection.PropertyMappings
                        .Select(mapping => (mapping.SourceProperty.Trim(), mapping.TargetProperty.Trim()))
                        .ToList()
                    : sourceProperties.Keys
                        .Intersect(targetProperties.Keys, StringComparer.Ordinal)
                        .OrderBy(property => property, StringComparer.Ordinal)
                        .Select(property => (property, property))
                        .ToList();

                if (ParseNodeKind(model.Tasks[nodeIndex[connection.ConnectedBlueprintNodeId]].NodeKind) == BlueprintNodeKind.ConditionalBranch &&
                    (effectiveMappings.Count != sourceProperties.Count || effectiveMappings.Any(m => m.Item1 != m.Item2)))
                    result.Add("graph.conditionalBranch.mapping", "A Conditional Branch receives every field with its original name.",
                        $"tasks[{nodeIndex[task.InstanceId]}].outgoingConnections[{connectionIndex}].propertyMappings");

                var route = routes?.Output(task.InstanceId, connection.ChannelName ?? "") ?? BlueprintRouteAvailability.Always;
                foreach (var (sourceProperty, targetProperty) in effectiveMappings)
                {
                    if (!sourceProperties.ContainsKey(sourceProperty))
                    {
                        result.Add(
                            "graph.connection.mapping.sourceMissing",
                            $"Source property '{sourceProperty}' does not exist on channel '{connection.ChannelName}'.",
                            $"tasks[{nodeIndex[task.InstanceId]}].outgoingConnections[{connectionIndex}].propertyMappings");
                    }

                    if (!targetProperties.ContainsKey(targetProperty))
                    {
                        result.Add(
                            "graph.connection.mapping.targetMissing",
                            $"Target property '{targetProperty}' does not exist on the destination input channel.",
                            $"tasks[{nodeIndex[task.InstanceId]}].outgoingConnections[{connectionIndex}].propertyMappings");
                        continue;
                    }

                    if (sourceProperties.TryGetValue(sourceProperty, out var sourceKind) &&
                        targetProperties.TryGetValue(targetProperty, out var targetKind) &&
                        !string.Equals(sourceKind, targetKind, StringComparison.Ordinal))
                    {
                        result.Add(
                            "graph.connection.mapping.typeMismatch",
                            $"Source property '{sourceProperty}' ({sourceKind}) cannot be mapped to destination property '{targetProperty}' ({targetKind}).",
                            $"tasks[{nodeIndex[task.InstanceId]}].outgoingConnections[{connectionIndex}].propertyMappings");
                        continue;
                    }

                    var key = (connection.ConnectedBlueprintNodeId, targetProperty);
                    var overlaps = coverage.TryGetValue(key, out var previous) && (routes?.Overlap(previous, route) ?? true);
                    coverage[key] = routes?.Union(previous, route) ?? BlueprintRouteAvailability.Always;
                    if (overlaps)
                    {
                        result.Add(
                            "graph.connection.mapping.duplicateTarget",
                            $"Destination input property '{targetProperty}' is assigned by multiple inbound connections.",
                            $"tasks[{nodeIndex[task.InstanceId]}].outgoingConnections[{connectionIndex}].propertyMappings");
                    }
                }
            }
        }

        if (routes is not null)
        {
            foreach (var (target, properties) in requiredInputs)
            {
                if (target == model.StartingBlueprintNodeId) continue;
                foreach (var property in properties)
                {
                    if (!model.Tasks[nodeIndex[target]].DesignTimeInputOverrides.ContainsKey(property) &&
                        coverage.GetValueOrDefault((target, property)) != routes.NodeRoute(target))
                        result.Add("graph.route.requiredMissing", $"Required input '{property}' needs a mapping on every route to this Step.",
                            $"tasks[{nodeIndex[target]}]");
                }
            }
        }
        return result;
    }

    private static BlueprintNodeKind ParseNodeKind(string? value) =>
        Enum.TryParse<BlueprintNodeKind>(value, ignoreCase: true, out var parsed)
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
}
