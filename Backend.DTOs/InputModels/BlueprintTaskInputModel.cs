using System.ComponentModel.DataAnnotations;
using Backend.Models.ViewModels;

namespace Backend.Models.InputModels;

public sealed class BlueprintTaskInputModel
{
    /// <summary>
    /// Optional persistent ID for the task instance (maps to TaskInstances.Id).
    /// Usually omitted on create; can be accepted if clients generate GUIDs.
    /// </summary>
    public Guid InstanceId { get; init; }

    /// <summary>
    /// The TaskDefinitions.Id this task instance uses.
    /// This identifies the task definition; the allowed compatibility lane is expressed by AllowedMajor/AllowedMinor.
    /// </summary>
    [Required]
    [Range(1, int.MaxValue)]
    public int TaskDefinitionId { get; init; }

    [Required]
    [Range(0, int.MaxValue)]
    public int AllowedMajor { get; init; }

    [Required]
    [Range(0, int.MaxValue)]
    public int AllowedMinor { get; init; }

    [Required]
    public CoordinatesViewModel Position { get; init; }

    /// <summary>Outgoing connections from this task.</summary>
    public List<BlueprintTaskConnectionInputModel> OutgoingConnections { get; init; } = new();
}
