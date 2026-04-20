namespace Backend.Models.Realtime;

public sealed class ProcessRealtimeBatch
{
    public List<ProcessSummaryUpdatedEvent> Summaries { get; init; } = [];
    public List<ProcessStepsUpdatedEvent> NodeUpdates { get; init; } = [];
}
