namespace Backend.Database.Relational.Entities;

public class BlueprintNode
{
    public Guid Id { get; set; }

    public int BlueprintVersionId { get; set; }
    public BlueprintVersion BlueprintVersion { get; set; } = null!;

    public int TaskDefinitionId { get; set; }
    public TaskDefinition TaskDefinition { get; set; } = null!;

    public int AllowedMajor { get; set; }
    public int AllowedMinor { get; set; }

    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public string? DesignTimeInputOverridesJson { get; set; }

    public List<BlueprintConnection> OutgoingConnections { get; set; } = new();
    public List<BlueprintConnection> IncomingConnections { get; set; } = new();
}
