namespace Backend.Models.ViewModels;

public sealed class ProcessObservabilitySummaryViewModel
{
    public required string Window { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public int SuccessfulCount { get; init; }
    public int FailedCount { get; init; }
    public int TotalCount => SuccessfulCount + FailedCount;
}
