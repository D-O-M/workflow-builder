namespace Backend.Models.ViewModels;

public sealed class ProcessStepViewModel
{
    public required Guid InstanceId { get; init; }
    public string NodeKind { get; init; } = "PluginTask";
    public string? CollectionIteratorItemKind { get; init; }
    public int RuntimeStepCount { get; init; }
    public required int TaskDefinitionId { get; init; }
    public required int AllowedMajor { get; init; }
    public required int AllowedMinor { get; init; }
    public required int ResolvedVersionId { get; init; }
    public string? ResolvedVersion { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public required CoordinatesViewModel Position { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? FailedAt { get; init; }
    public string? FailureMessage { get; init; }
    public string? InputChannelName { get; init; }
    public List<string> OutputChannelNames { get; init; } = [];
    public List<BlueprintConnectionViewModel> OutgoingConnections { get; init; } = [];
}
