namespace Backend.Models.Realtime;

public sealed class ExecutionNodeDelta
{
    public required Guid InstanceId { get; init; }
    public required Guid TaskInstanceId { get; init; }
    public required string Status { get; init; }
    public required int ResolvedVersionId { get; init; }
    public string? ResolvedVersion { get; init; }
    public string? FailureMessage { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}
