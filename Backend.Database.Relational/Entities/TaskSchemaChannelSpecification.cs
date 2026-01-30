namespace Backend.Database.Relational.Entities;

public class TaskSchemaChannelSpecification
{
    public int TaskSchemaId { get; set; }
    public TaskSchema TaskSchema { get; set; } = null!;
    public int ChannelSpecificationId { get; set; }
    public ChannelSpecification ChannelSpecification { get; set; } = null!;
}
