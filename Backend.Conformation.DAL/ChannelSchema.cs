namespace Backend.Relational.DAL;

public record ChannelSchema
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public List<ChannelParameterSchema> Parameters { get; init; } = new();
}
