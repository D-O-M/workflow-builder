namespace Backend.Models;

public record Channel(
    int Id,
    string Name,
    //WorkflowTask? SourceNode,
    //WorkflowTask? DestinationNode,
    List<ChannelParameter> Parameters);
