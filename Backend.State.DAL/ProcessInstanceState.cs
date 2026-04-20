using Backend.Database.State;

namespace Backend.State.DAL;

public class ProcessInstanceState
{
    public required Guid ProcessId { get; init; }
    public required int ExecutableBlueprintId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public Stream? GlobalStateDataStream { get; init; }
    public required List<ProcessStepState> Tasks { get; init; }
}

public class ProcessStepState
{
    public required Guid ProcessStepId { get; init; }
    public TaskExecutionResult? Result { get; set; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required Stream InputChannelStateDataStream { get; init; }
    public Dictionary<string, Stream> OutputChannelStateDataStreams { get; } = new();
}
