namespace Backend.Database.Relational.Entities;

public record WorkflowTask
{
    public int Id { get; init; }
    public int SchemaId { get; init; }
    public TaskSchema? Schema { get; init; }
    public int BlueprintId { get; init; }
    public WorkflowBlueprint? Blueprint { get; init; }
    public WorkflowTaskChannel InputChannel { get; init; } = null!;
    public List<WorkflowTaskChannel> OutputChannels { get; init; } = [];
}
