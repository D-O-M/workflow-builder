namespace Backend.Database.Relational.Entities;

public class TaskDefinition
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public string? DisplayName { get; set; }

    public required List<TaskVersion> Versions { get; set; }
}
