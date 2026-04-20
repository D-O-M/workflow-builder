namespace Backend.Models.ViewModels;

public sealed class BlueprintNodeViewModel
{
    /// <summary>Persistent task instance id (BlueprintNodes.Id).</summary>
    public Guid InstanceId { get; init; }

    public int TaskDefinitionId { get; init; }
    public int AllowedMajor { get; init; }
    public int AllowedMinor { get; init; }
    public int ResolvedVersionId { get; init; }
    public string? ResolvedVersion { get; init; }

    public CoordinatesViewModel Position { get; init; }

    public List<BlueprintConnectionViewModel> OutgoingConnections { get; init; } = new();
}
