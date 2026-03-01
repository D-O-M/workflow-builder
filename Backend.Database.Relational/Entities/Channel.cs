namespace Backend.Database.Relational.Entities;

public abstract class Channel
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;

    public int TaskVersionId { get; set; }
    public TaskVersion TaskVersion { get; set; } = null!;

    public List<ChannelProperty> Properties { get; set; } = new();
}

public class InputChannelDefinition : Channel { }
public class OutputChannelDefinition : Channel { }
public class GlobalStateDefinition : Channel { }
