namespace Backend.Database.Relational.Entities;

public record WorkflowTaskChannel
{
    public int Id { get; init; }
    public int SchemaId { get; init; }
    public ChannelSpecification? Schema { get; init; }
    public int? SourceNodeId { get; init; }
    public WorkflowTask? SourceNode { get; init; }
    public int? DestinationNodeId { get; init; }
    public WorkflowTask? DestinationNode { get; init; }
}
