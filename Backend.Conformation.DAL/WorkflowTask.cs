namespace Backend.Relational.DAL;

public record WorkflowTask
{
    public int Id { get; init; }
    public int TaskSchemaId { get; init; }
    public TaskSchema? TaskSchema { get; init; }
    public int WorkflowId { get; init; }
    public Workflow? Workflow { get; init; }
    public List<Channel> InputChannels { get; init; } = new();
    public List<Channel> OutputChannels { get; init; } = new();
}
