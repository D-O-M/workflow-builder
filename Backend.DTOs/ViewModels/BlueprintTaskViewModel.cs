namespace Backend.DTOs.ViewModels;

public sealed class BlueprintTaskViewModel
{
    /// <summary>Persistent task instance id (TaskInstances.Id).</summary>
    public Guid InstanceId { get; init; }

    /// <summary>
    /// The TaskVersions.Id used by this task instance (and the reference id used in connections).
    /// </summary>
    public int VersionId { get; init; }

    public CoordinatesViewModel Position { get; init; }

    public List<BlueprintTaskConnectionViewModel> OutgoingConnections { get; init; } = new();
}
