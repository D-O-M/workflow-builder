namespace Backend.Database.Relational.Entities;

public record ChannelPropertyCriterion
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string Type { get; init; } = null!;
    public int ChannelId { get; init; }
}
