namespace Backend.Database.Relational.Entities;

public class BlueprintVersion
{
    public int Id { get; set; }

    public int BlueprintDefinitionId { get; set; }
    public BlueprintDefinition BlueprintDefinition { get; set; } = null!;

    public int Version { get; set; }
    public string Name { get; set; } = null!;

    public Guid? StartingBlueprintNodeId { get; set; }
    public BlueprintNode? StartingBlueprintNode { get; set; }

    public List<BlueprintNode> BlueprintNodes { get; set; } = new();
}
