namespace Backend.Database.Relational.Entities;

public enum WorkflowPropertyKind
{
    String = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    Guid = 4,
    Date = 5,
    DateTime = 6,
    Enum = 7
}

public class ChannelProperty
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public WorkflowPropertyKind Kind { get; set; }
    public bool IsCollection { get; set; }
    public bool IsRequired { get; set; }
    public string? EnumValuesJson { get; set; }

    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
}
