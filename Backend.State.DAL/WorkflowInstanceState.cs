using Backend.Database.State;

namespace Backend.State.DAL;

public class WorkflowInstanceState
{
    public required Guid WorkflowInstanceId { get; init; }
    public required int WorkflowBlueprintId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public Stream? GlobalStateDataStream { get; init; }
    public required List<WorkflowTaskInstanceState> Tasks { get; init; }
}

public class WorkflowTaskInstanceState
{
    public required Guid WorkflowTaskInstanceId { get; init; }
    public required TaskExecutionResult Result { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required Stream InputChannelStateDataStream { get; init; }
    public Dictionary<string, Stream> OutputChannelStateDataStreams { get; } = new();
}
