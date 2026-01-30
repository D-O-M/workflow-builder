namespace Backend.Database.Relational.Entities;

public record WorkflowBlueprint
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public int StartingTaskId { get; init; }
    public List<WorkflowTask> Tasks { get; init; } = new();
}
