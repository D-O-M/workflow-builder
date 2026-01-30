namespace Backend.Database.Relational.Entities;

public record TaskSchema
{
    public int Id { get; set; }
    public string Name { get; init; } = null!;
    public string Path { get; init; } = null!;
    public Version Version { get; init; } = null!;
    public int? InputChannelSpecificationId { get; init; }
    public ChannelSpecification? InputChannelSpecification { get; init; }
    public List<ChannelSpecification> OutputChannelSpecifications { get; init; } = new();
}
