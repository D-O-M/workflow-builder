namespace Backend.Conformation.DAL;
public record WorkflowTask(
    string Name,
    string Path,
    Version Version)
{
    public int Id { get; set; }
    public List<Channel> InputChannels { get; init; } = new();
    public List<Channel> OutputChannels { get; init; } = new();
}
