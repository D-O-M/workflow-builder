namespace Backend.Database.Relational.Entities;

public record ChannelSpecification
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public int TaskSchemaId { get; init; }
    public TaskSchema? TaskSchema { get; init; }
    public List<ChannelPropertyCriterion> Properties { get; init; } = new();
}
