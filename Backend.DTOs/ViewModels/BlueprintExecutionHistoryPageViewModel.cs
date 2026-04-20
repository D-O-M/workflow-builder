namespace Backend.Models.ViewModels;

public sealed class BlueprintExecutionHistoryPageViewModel
{
    public required IReadOnlyList<BlueprintExecutionHistoryEntryViewModel> Items { get; init; }
    public string? NextCursor { get; init; }
    public required bool HasMore { get; init; }
}
