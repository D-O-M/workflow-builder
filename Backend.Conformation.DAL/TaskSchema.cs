namespace Backend.Relational.DAL;

public record TaskSchema
{
    public int Id { get; set; }
    public string Name { get; init; } = null!;
    public string Path { get; init; } = null!;
    public Version Version { get; init; } = null!;
    public List<ChannelSchema> InputChannels { get; init; } = new();
    public List<ChannelSchema> OutputChannels { get; init; } = new();
}
