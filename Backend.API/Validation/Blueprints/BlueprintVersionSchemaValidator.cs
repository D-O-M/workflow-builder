using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
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
        var result = new BlueprintValidationResult();
        if (model.Tasks.Count == 0)
        {
            return result;
        }

        var nodeIndex = model.Tasks
            .Select((task, index) => new { task.InstanceId, index })
            .ToDictionary(item => item.InstanceId, item => item.index);
        var taskDefinitionIds = model.Tasks
            .Select(task => task.TaskDefinitionId)
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

        foreach (var task in model.Tasks)
        {
            if (!taskDefinitionsById.TryGetValue(task.TaskDefinitionId, out var taskDefinition))
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

            inputPropertiesByNodeId[task.InstanceId] = inputChannel.Properties.ToDictionary(
                property => property.Name,
                property => property.Kind.ToString(),
                StringComparer.Ordinal);
            outputPropertiesByNodeId[task.InstanceId] = resolvedVersion.OutputChannels.ToDictionary(
                channel => channel.Name,
                channel => channel.Properties.ToDictionary(
                    property => property.Name,
                    property => property.Kind.ToString(),
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

        var incomingTargetProperties = new Dictionary<Guid, HashSet<string>>();
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

                    if (!incomingTargetProperties.TryGetValue(connection.ConnectedBlueprintNodeId, out var targetSet))
                    {
                        targetSet = new HashSet<string>(StringComparer.Ordinal);
                        incomingTargetProperties[connection.ConnectedBlueprintNodeId] = targetSet;
                    }

                    if (!targetSet.Add(targetProperty))
                    {
                        result.Add(
                            "graph.connection.mapping.duplicateTarget",
                            $"Destination input property '{targetProperty}' is assigned by multiple inbound connections.",
                            $"tasks[{nodeIndex[task.InstanceId]}].outgoingConnections[{connectionIndex}].propertyMappings");
                    }
                }
            }
        }

        return result;
    }
}
