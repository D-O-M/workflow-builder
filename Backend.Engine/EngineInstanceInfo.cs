namespace Backend.Engine;

public record EngineInstanceInfo
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? StoppedAt { get; set; }
}
