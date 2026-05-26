namespace Backend.Engine;

public sealed class EngineProjectionOptions
{
    public const string SectionName = "Projections";

    public bool SynchronousEngineUpdatesEnabled { get; init; } = true;

    public static EngineProjectionOptions Enabled { get; } = new()
    {
        SynchronousEngineUpdatesEnabled = true
    };
}
