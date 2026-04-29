namespace Backend.Engine;

public sealed class CollectionIteratorOptions
{
    public int MaxItems { get; set; } = 1_000;

    public int BatchSize { get; set; } = 100;
}
