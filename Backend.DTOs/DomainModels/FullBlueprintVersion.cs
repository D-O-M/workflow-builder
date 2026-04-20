namespace Backend.Models.DomainModels;

public class FullBlueprintVersion
{
    public int Id { get; set; }
    public int DefinitionId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public int Version { get; set; }
    public int StartingBlueprintNodeId { get; set; }
    public required BlueprintTaskTree TaskTree { get; set; }

    public Dictionary<Guid, BlueprintTaskTree> TasksByInstanceId
        => (_lazyTasksByInstanceId ??= CreateLazy()).Value;

    // Keep Lazy private (nullable so we can create it only when TaskTree is ready)
    private Lazy<Dictionary<Guid, BlueprintTaskTree>>? _lazyTasksByInstanceId;

    private Lazy<Dictionary<Guid, BlueprintTaskTree>> CreateLazy()
        => new Lazy<Dictionary<Guid, BlueprintTaskTree>>(
            () => BuildTasksByInstanceId(TaskTree),
            isThreadSafe: true);

    private static Dictionary<Guid, BlueprintTaskTree> BuildTasksByInstanceId(BlueprintTaskTree root)
    {
        var dict = new Dictionary<Guid, BlueprintTaskTree>();
        var visited = new HashSet<Guid>();
        Visit(root, dict, visited);
        return dict;
    }

    private static void Visit(
        BlueprintTaskTree node,
        Dictionary<Guid, BlueprintTaskTree> dict,
        HashSet<Guid> visited)
    {
        if (!visited.Add(node.InstanceId))
            return;

        dict[node.InstanceId] = node;

        foreach (var conn in node.OutgoingConnections)
        {
            var child = conn.Task;
            if (child is null) continue;
            Visit(child, dict, visited);
        }
    }
}

public class BlueprintTaskTree
{
    public Guid InstanceId { get; set; }
    public int TaskDefinitionId { get; set; }
    public int AllowedMajor { get; set; }
    public int AllowedMinor { get; set; }
    public Coordinates Position { get; set; }
    public List<BlueprintConnection> OutgoingConnections { get; set; } = new();
}

public readonly record struct Coordinates(int X, int Y);

public class ChannelDetails
{
    public int? ChannelId { get; set; }
    public string? ChannelName { get; set; }
}

public class BlueprintConnection
{
    public required ChannelDetails ChannelDetails { get; set; }
    public int Anchor { get; set; }
    public required string Side { get; set; }
    public int Position { get; set; }
    public BlueprintTaskTree? Task { get; set; }
}
