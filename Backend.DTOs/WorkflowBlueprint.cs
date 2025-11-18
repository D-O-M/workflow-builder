namespace Backend.DTOs;

public record WorkflowBlueprint
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public required WorkflowTask InitialTask { get; set; }
    public required List<WorkflowTask> AllTasks { get; set; }
}

public record WorkflowTask
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required Version Version { get; set; }
    public int SchemaId { get; set; }
    public required WorkflowTaskChannel InputChannel { get; set; }
    public required Dictionary<string, WorkflowTaskChannel> OutputChannels { get; set; }
}

public record WorkflowTaskChannel
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required Dictionary<string, string> Criterion { get; set; }
    public WorkflowTask? Source { get; set; }
    public WorkflowTask? Destination { get; set; }
}
