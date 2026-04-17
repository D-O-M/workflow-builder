namespace Backend.Models.ViewModels;

public sealed class WorkflowExecutionNodeViewModel
{
    public required Guid InstanceId { get; init; }
    public required int VersionId { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required CoordinatesViewModel Position { get; init; }
    public string? FailureMessage { get; init; }
    public string? InputChannelName { get; init; }
    public List<string> OutputChannelNames { get; init; } = [];
    public List<BlueprintTaskConnectionViewModel> OutgoingConnections { get; init; } = [];
}
