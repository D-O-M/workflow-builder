namespace Backend.Models.ViewModels;

public sealed class BlueprintProcessHistoryPageViewModel
{
    public required IReadOnlyList<BlueprintProcessHistoryEntryViewModel> Items { get; init; }
    public string? NextCursor { get; init; }
    public required bool HasMore { get; init; }
}
