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
    /// The TaskVersions.Id this task instance uses.
    /// Also serves as the intra-payload reference id for connections; must be unique in tasks[] (validate in service layer).
    /// </summary>
    [Required]
    [Range(1, int.MaxValue)]
    public int VersionId { get; init; }

    [Required]
    public CoordinatesViewModel Position { get; init; }

    /// <summary>Outgoing connections from this task.</summary>
    public List<BlueprintTaskConnectionInputModel> OutgoingConnections { get; init; } = new();
}
