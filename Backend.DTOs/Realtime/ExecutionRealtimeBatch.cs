namespace Backend.Models.Realtime;

public sealed class ExecutionRealtimeBatch
{
    public List<ExecutionSummaryUpdatedEvent> Summaries { get; init; } = [];
    public List<ExecutionNodesUpdatedEvent> NodeUpdates { get; init; } = [];
}
