namespace Backend.Database.Relational.Entities;

public class TaskInstance
{
    public Guid Id { get; set; }

    public int BlueprintVersionId { get; set; }
    public BlueprintVersion BlueprintVersion { get; set; } = null!;

    public int TaskVersionId { get; set; }
    public TaskVersion TaskVersion { get; set; } = null!;

    public int PositionX { get; set; }
    public int PositionY { get; set; }

    public List<TaskConnection> OutgoingConnections { get; set; } = new();
    public List<TaskConnection> IncomingConnections { get; set; } = new();
}
