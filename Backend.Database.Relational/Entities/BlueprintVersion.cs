namespace Backend.Database.Relational.Entities;

public class BlueprintVersion
{
    public int Id { get; set; }

    public int BlueprintDefinitionId { get; set; }
    public BlueprintDefinition BlueprintDefinition { get; set; } = null!;

    public int Version { get; set; }
    public string Name { get; set; } = null!;

    public Guid StartingTaskInstanceId { get; set; }
    public TaskInstance StartingTaskInstance { get; set; } = null!;

    public List<TaskInstance> TaskInstances { get; set; } = new();
}
