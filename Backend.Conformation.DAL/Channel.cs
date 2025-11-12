namespace Backend.Relational.DAL;

public record Channel
{
    public int Id { get; init; }
    public int ChannelSchemaId { get; init; }
    public ChannelSchema? ChannelSchema { get; init; }
    public int? SourceNodeId { get; init; }
    public WorkflowTask? SourceNode { get; init; }
    public int? DestinationNodeId { get; init; }
    public WorkflowTask? DestinationNode { get; init; }
}
