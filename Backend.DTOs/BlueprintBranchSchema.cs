using System.Text.Json;
using Backend.Database.Relational.Entities;
using Backend.Models.InputModels;
using Backend.Models.Versioning;

namespace Backend.Models;

/// <summary>Resolves native pass-through channels from the upstream versioned Plugin contract.</summary>
public sealed class BlueprintBranchSchema
{
    private readonly ILookup<Guid, (BlueprintNodeInputModel Node, BlueprintConnectionInputModel Edge)> _incoming;
    private readonly IReadOnlyDictionary<int, TaskDefinition> _definitions;
    private readonly Dictionary<(Guid Node, string? Channel), List<ConditionalField>> _outputs = [];

    public BlueprintBranchSchema(IEnumerable<BlueprintNodeInputModel> nodes, IReadOnlyDictionary<int, TaskDefinition> definitions)
    {
        _incoming = nodes.SelectMany(node => node.OutgoingConnections.Select(edge => (Node: node, Edge: edge)))
            .ToLookup(entry => entry.Edge.ConnectedBlueprintNodeId);
        _definitions = definitions;
    }

    public List<ConditionalField> Resolve(Guid branchId)
    {
        var incoming = _incoming[branchId].ToArray();
        if (incoming.Length != 1)
            throw new InvalidDataException("Conditional Branch requires exactly one incoming output channel.");
        return Output(incoming[0].Node, incoming[0].Edge.ChannelName, [branchId]);
    }

    private List<ConditionalField> Output(BlueprintNodeInputModel node, string? channel, HashSet<Guid> visited)
    {
        var key = (node.InstanceId, channel);
        if (_outputs.TryGetValue(key, out var cached)) return cached;
        var fields = ReadOutput(node, channel, visited);
        _outputs.Add(key, fields);
        return fields;
    }

    private List<ConditionalField> ReadOutput(BlueprintNodeInputModel node, string? channel, HashSet<Guid> visited)
    {
        if (!visited.Add(node.InstanceId))
            throw new InvalidDataException("A conditional input cannot depend on a cycle.");
        Enum.TryParse<BlueprintNodeKind>(node.NodeKind, true, out var kind);
        if (kind == BlueprintNodeKind.ConditionalBranch)
        {
            var parents = _incoming[node.InstanceId].ToArray();
            if (channel is not ("True" or "False") || parents.Length != 1)
                throw new InvalidDataException("Conditional Branch requires a single incoming channel and True or False output.");
            return Output(parents[0].Node, parents[0].Edge.ChannelName, visited);
        }
        if (kind == BlueprintNodeKind.CollectionIterator)
        {
            if (channel != "Item")
                throw new InvalidDataException("Collection Iterator exposes its item on the Item channel.");
            var item = new ConditionalField { Name = "item", Kind = node.CollectionIteratorItemKind ?? "String", Required = true };
            if (item.Kind == "Enum")
            {
                // Alternative routes can both map a collection to the same iterator.
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (source, edge) in _incoming[node.InstanceId])
                {
                    var property = edge.PropertyMappings.Count == 0 ? "items" :
                        edge.PropertyMappings.FirstOrDefault(mapping => mapping.TargetProperty == "items")?.SourceProperty;
                    if (property is null) continue;
                    var field = Output(source, edge.ChannelName, new HashSet<Guid>(visited))
                        .FirstOrDefault(candidate => candidate.Name == property && candidate.Kind == "Enum[]");
                    if (field is not null) names.UnionWith(field.EnumValues);
                }
                item.EnumValues = names.Order(StringComparer.Ordinal).ToList();
            }
            return [item];
        }

        if (!_definitions.TryGetValue(node.TaskDefinitionId ?? 0, out var definition))
            throw new InvalidDataException("The upstream task definition is unavailable.");
        var version = definition.Versions.Where(v => SemanticVersionUtility.IsSameLane(v.Version, node.AllowedMajor, node.AllowedMinor))
            .OrderByDescending(v => v.Version).FirstOrDefault();
        var output = version?.OutputChannels.FirstOrDefault(c => c.Name == channel)
            ?? throw new InvalidDataException($"The upstream output channel '{channel}' is unavailable.");
        return output.Properties.Select(property => new ConditionalField
        {
            Name = property.Name,
            Kind = WorkflowPropertyTypeName.Format(property.Kind, property.IsCollection),
            Required = property.IsRequired,
            EnumValues = property.EnumValuesJson is null ? [] : JsonSerializer.Deserialize<List<string>>(property.EnumValuesJson) ?? []
        }).ToList();
    }
}
