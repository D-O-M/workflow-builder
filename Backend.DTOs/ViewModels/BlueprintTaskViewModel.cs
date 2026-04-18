namespace Backend.Models.ViewModels;

public sealed class BlueprintTaskViewModel
{
    /// <summary>Persistent task instance id (TaskInstances.Id).</summary>
    public Guid InstanceId { get; init; }

    public int TaskDefinitionId { get; init; }
    public int AllowedMajor { get; init; }
    public int AllowedMinor { get; init; }
    public int ResolvedVersionId { get; init; }
    public string? ResolvedVersion { get; init; }

    public CoordinatesViewModel Position { get; init; }

    public List<BlueprintTaskConnectionViewModel> OutgoingConnections { get; init; } = new();
}
