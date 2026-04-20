namespace Backend.Models.ViewModels;

public sealed class BlueprintProcessActivityPageViewModel
{
    public required IReadOnlyList<BlueprintProcessActivityViewModel> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }
    public required bool HasMore { get; init; }
    public required int RankingWindowHours { get; init; }
}
