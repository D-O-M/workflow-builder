namespace Backend.Relational.DAL;

public record ChannelParameterSchema
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string Type { get; init; } = null!;
    public int ChannelId { get; init; }
    public ChannelSchema Channel { get; init; } = null!;
}
