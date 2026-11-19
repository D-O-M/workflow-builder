using System.Text.Json;

namespace Backend.Models;

public record ExecutableBlueprint
{
    private IReadOnlyDictionary<Guid, ExecutableBlueprintNode>? _nodesById;

    public int Id { get; set; }
    public int DefinitionId { get; set; }
    public int VersionNumber { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public required ExecutableBlueprintNode InitialNode { get; set; }
    public required List<ExecutableBlueprintNode> AllNodes { get; set; }

    public IReadOnlyDictionary<Guid, ExecutableBlueprintNode> NodesById
        => _nodesById ??= AllNodes.ToDictionary(node => node.Id);
}

public enum ExecutableBlueprintNodeKind
{
    PluginTask,
    CollectionIterator,
    ConditionalBranch
}

public record ExecutableBlueprintNode
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public ExecutableBlueprintNodeKind NodeKind { get; set; } = ExecutableBlueprintNodeKind.PluginTask;
    public int TaskDefinitionId { get; set; }
    public ConditionalRule? ConditionalRule { get; set; }
    public List<ConditionalField> ConditionalFields { get; set; } = [];
    public string? CollectionIteratorItemKind { get; set; }
    public int AllowedMajor { get; set; }
    public int AllowedMinor { get; set; }
    public required Version ResolvedVersion { get; set; }
    public int ResolvedTaskVersionId { get; set; }
    public int ExecutionOrder { get; set; }
    public required ExecutableBlueprintChannel InputChannel { get; set; }
    public required Dictionary<string, ExecutableBlueprintChannel> OutputChannels { get; set; }
    public required List<ExecutableBlueprintBinding> InboundBindings { get; set; }
    public required Dictionary<string, JsonElement> DesignTimeInputOverrides { get; set; }
}

public record ExecutableBlueprintChannel
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required Dictionary<string, string> Properties { get; set; }
    public List<string> RequiredProperties { get; set; } = [];
    public required List<ExecutableBlueprintBinding> OutboundBindings { get; set; }
}

public record ExecutableBlueprintBinding
{
    public required string Id { get; set; }
    public required Guid SourceBlueprintNodeId { get; set; }
    public required string SourceChannelName { get; set; }
    public required Guid DestinationBlueprintNodeId { get; set; }
    public required List<ExecutableBlueprintPropertyMapping> PropertyMappings { get; set; }
}

public record ExecutableBlueprintPropertyMapping
{
    public required string SourceProperty { get; set; }
    public required string TargetProperty { get; set; }
}
