namespace Backend.Database.Relational.Entities;

public class TaskInstance
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

    public List<TaskConnection> OutgoingConnections { get; set; } = new();
    public List<TaskConnection> IncomingConnections { get; set; } = new();
}
