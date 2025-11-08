namespace Backend.Conformation.DAL;

public record Workflow(
    int Id,
    string Name,
    string? Description,
    int StartingTaskId
)
{
    public List<WorkflowTask> Tasks { get; init; } = new();
    public List<Channel> Channels { get; init; } = new();
}
