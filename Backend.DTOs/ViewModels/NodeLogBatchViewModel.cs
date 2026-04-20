namespace Backend.Models.ViewModels;

public sealed class NodeLogBatchViewModel
{
    public List<NodeLogEntryViewModel> Entries { get; init; } = [];
    public string? NextCursor { get; init; }
}

public sealed class NodeLogEntryViewModel
{
    public required string Cursor { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Message { get; init; }
    public string? ServiceName { get; init; }
    public string? Level { get; init; }
}
