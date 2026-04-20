namespace Backend.Database.State.Objects;

public sealed class ProcessStateSummaryPage
{
    public required IReadOnlyList<ProcessStateSummary> Items { get; init; }
    public string? ContinuationToken { get; init; }
}
