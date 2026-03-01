namespace Backend.Database.Relational.Entities;

public enum PrimitiveTypes
{
    Number = 0,
    String = 1,
    Guid = 2,
    Boolean = 3
}

public class ChannelProperty
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public PrimitiveTypes Type { get; set; }

    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
}
