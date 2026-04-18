namespace Backend.Database.Relational.Entities;

using System.ComponentModel.DataAnnotations.Schema;

public class TaskVersion
{
    public int Id { get; set; }
    public string? Description { get; set; }

    public int TaskDefinitionId { get; set; }
    public TaskDefinition? TaskDefinition { get; set; }

    public required Version Version { get; set; }
    public required string Path { get; set; }
    public TaskVersionState State { get; set; } = TaskVersionState.Registered;

    public List<Channel> Channels { get; set; } = new();

    [NotMapped]
    public GlobalStateDefinition? GlobalState
    {
        get
        {
            var globalStates = Channels.OfType<GlobalStateDefinition>().ToList();
            return globalStates.Count switch
            {
                0 => null,
                1 => globalStates[0],
                _ => throw new InvalidOperationException($"More than one GlobalStateDefinition exists for TaskVersionId {Id}.")
            };
        }
        set
        {
            var existing = Channels.OfType<GlobalStateDefinition>().ToList();
            foreach (var gs in existing)
                Channels.Remove(gs);

            if (value is not null)
                Channels.Add(value);
        }
    }

    [NotMapped]
    public IEnumerable<InputChannelDefinition> InputChannels => Channels.OfType<InputChannelDefinition>();

    [NotMapped]
    public IEnumerable<OutputChannelDefinition> OutputChannels => Channels.OfType<OutputChannelDefinition>();
}
