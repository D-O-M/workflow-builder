namespace Backend.Models.DomainModels;

public record ChannelDefinitionDomainModel
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ModelTypeName { get; init; }
    public required HashSet<PropertyDefinitionDomainModel> Properties { get; init; }
}
