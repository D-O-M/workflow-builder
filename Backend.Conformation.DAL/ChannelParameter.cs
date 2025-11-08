namespace Backend.Conformation.DAL;

public record ChannelParameter(
    string Name,
    string Type)
{
    public int Id { get; init; }
    public int ChannelId { get; init; }
    public Channel Channel { get; init; } = null!;
}
