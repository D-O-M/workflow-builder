namespace Backend.API;

public class WorkflowTask
{
    public required string Name { get; set; }
    public int Channels { get; set; }
    public required string Description { get; set; }
}
