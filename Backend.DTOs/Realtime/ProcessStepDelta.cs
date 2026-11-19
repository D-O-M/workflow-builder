namespace Backend.Models.Realtime;

public sealed class ProcessStepDelta
{
    public required Guid InstanceId { get; init; }
    public required Guid BlueprintNodeId { get; init; }
    public string? SelectedChannel { get; init; }
    public List<ViewModels.ProcessStepIterationViewModel> Iterations { get; init; } = [];
    public required string Status { get; init; }
    public required int ResolvedVersionId { get; init; }
    public string? ResolvedVersion { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public string? FailureMessage { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
