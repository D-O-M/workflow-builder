namespace Backend.Models.ViewModels;

public sealed class WorkflowLogBatchViewModel
{
    public List<WorkflowLogEntryViewModel> Entries { get; init; } = [];
    public string? NextCursor { get; init; }
}

public sealed class WorkflowLogEntryViewModel
{
    public required string Cursor { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Message { get; init; }
    public Guid? WorkflowTaskInstanceId { get; init; }
    public Guid? BlueprintTaskId { get; init; }
    public string? TaskName { get; init; }
    public string? PluginName { get; init; }
    public string? ServiceName { get; init; }
    public string? Level { get; init; }
}
