using Backend.Database.Relational.Entities;

namespace Backend.Models.DomainModels;

public sealed record PropertyDefinitionDomainModel
{
    public required string Name { get; init; }
    public required WorkflowPropertyKind Kind { get; init; }
    public bool IsCollection { get; init; }
    public required bool IsRequired { get; init; }
    public required IReadOnlyList<string> EnumValues { get; init; }
}
