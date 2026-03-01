namespace Backend.Models.DomainModels;

public record ChannelDefinitionDTO
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string ModelTypeName { get; init; }
    public required HashSet<PropertyDefinitionDTO> Properties { get; init; }
}
