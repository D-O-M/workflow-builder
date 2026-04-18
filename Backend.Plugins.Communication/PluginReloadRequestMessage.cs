namespace Backend.Plugins.Communication;

public sealed record PluginReloadRequestMessage
{
    public int TaskDefinitionId { get; init; }
    public int TaskVersionId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ChangeKind { get; init; } = string.Empty;
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
}
