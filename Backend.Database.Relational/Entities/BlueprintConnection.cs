namespace Backend.Database.Relational.Entities;

public class BlueprintConnection
{
    public Guid Id { get; set; }

    public Guid? SourceBlueprintNodeId { get; set; }
    public BlueprintNode? SourceBlueprintNode { get; set; }

    public Guid? DestinationBlueprintNodeId { get; set; }
    public BlueprintNode? DestinationBlueprintNode { get; set; }

    public int Anchor { get; set; }
    public string Side { get; set; } = null!;
    public int Position { get; set; }
    public string ChannelName { get; set; } = null!;
}
