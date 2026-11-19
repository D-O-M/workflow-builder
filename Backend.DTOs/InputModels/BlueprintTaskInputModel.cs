using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Backend.Models.ViewModels;

namespace Backend.Models.InputModels;

public sealed class BlueprintNodeInputModel
{
    /// <summary>
    /// Optional persistent ID for the task instance (maps to BlueprintNodes.Id).
    /// Usually omitted on create; can be accepted if clients generate GUIDs.
    /// </summary>
    public Guid InstanceId { get; init; }

    public string NodeKind { get; init; } = "PluginTask";

    /// <summary>
    /// The TaskDefinitions.Id this task instance uses.
    /// This identifies the task definition; the allowed compatibility lane is expressed by AllowedMajor/AllowedMinor.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? TaskDefinitionId { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? PortableTaskKey { get; init; }
    public ConditionalRule? ConditionalRule { get; init; }
    public string? CollectionIteratorItemKind { get; init; }

    [Required]
    [Range(0, int.MaxValue)]
    public int AllowedMajor { get; init; }

    [Required]
    [Range(0, int.MaxValue)]
    public int AllowedMinor { get; init; }

    [Required]
    public CoordinatesViewModel Position { get; init; }

    public Dictionary<string, JsonElement> DesignTimeInputOverrides { get; init; } = new();

    /// <summary>Outgoing connections from this task.</summary>
    public List<BlueprintConnectionInputModel> OutgoingConnections { get; init; } = new();
}
