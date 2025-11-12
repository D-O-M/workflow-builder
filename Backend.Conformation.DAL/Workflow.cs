namespace Backend.Relational.DAL;

public record Workflow
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public int StartingTaskId { get; init; }
    public List<WorkflowTask> Tasks { get; init; } = new();
}
