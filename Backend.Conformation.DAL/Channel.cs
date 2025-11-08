namespace Backend.Conformation.DAL;

public record Channel(
    string Name,
    int? SourceNodeId,
    int? DestinationNodeId)
{
    public int Id { get; init; }
    public WorkflowTask? SourceNode { get; init; }
    public WorkflowTask? DestinationNode { get; init; }
    public List<ChannelParameter> Parameters { get; init; } = new();
}
