namespace Backend.Models;

public record ExecutableBlueprint
{
    public int Id { get; set; }
    public int DefinitionId { get; set; }
    public int VersionNumber { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public required ExecutableBlueprintNode InitialNode { get; set; }
    public required List<ExecutableBlueprintNode> AllNodes { get; set; }
}

public record ExecutableBlueprintNode
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public int TaskDefinitionId { get; set; }
    public int AllowedMajor { get; set; }
    public int AllowedMinor { get; set; }
    public required Version ResolvedVersion { get; set; }
    public int ResolvedTaskVersionId { get; set; }
    public required ExecutableBlueprintChannel InputChannel { get; set; }
    public required Dictionary<string, ExecutableBlueprintChannel> OutputChannels { get; set; }
}

public record ExecutableBlueprintChannel
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required Dictionary<string, string> Criterion { get; set; }
    public ExecutableBlueprintNode? Source { get; set; }
    public ExecutableBlueprintNode? Destination { get; set; }
}
