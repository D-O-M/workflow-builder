namespace Backend.API;

public sealed class TaskCatalogEntry
{
    public required string Name { get; set; }
    public int Channels { get; set; }
    public required string Description { get; set; }
}
