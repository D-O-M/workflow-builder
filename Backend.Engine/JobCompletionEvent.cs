namespace Backend.Engine;

public record JobCompletionEvent(
    Guid JobId,
    string[] OutputChannelsPopulated,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
