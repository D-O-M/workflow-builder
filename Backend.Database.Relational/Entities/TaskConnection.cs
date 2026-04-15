using System.Threading.Channels;

namespace Backend.Database.Relational.Entities;

public class TaskConnection
{
    public Guid Id { get; set; }

    public Guid? SourceTaskInstanceId { get; set; }
    public TaskInstance? SourceTaskInstance { get; set; }

    public Guid? DestinationTaskInstanceId { get; set; }
    public TaskInstance? DestinationTaskInstance { get; set; }

    public int Anchor { get; set; }
    public string Side { get; set; } = null!;
    public int Position { get; set; }

    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
}
