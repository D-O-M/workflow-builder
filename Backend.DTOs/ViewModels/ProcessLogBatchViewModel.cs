namespace Backend.Models.ViewModels;

public sealed class ProcessLogBatchViewModel
{
    public List<ProcessLogEntryViewModel> Entries { get; init; } = [];
    public string? NextCursor { get; init; }
}

public sealed class ProcessLogEntryViewModel
{
    public required string Cursor { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Message { get; init; }
    public Guid? ProcessStepId { get; init; }
    public Guid? BlueprintNodeId { get; init; }
    public string? TaskName { get; init; }
    public string? PluginName { get; init; }
    public string? ServiceName { get; init; }
    public string? Level { get; init; }
}
