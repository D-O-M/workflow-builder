using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using Backend.Models.ViewModels;

namespace Backend.Models.InputModels;

public sealed class ExportedBlueprintNodeInputModel
{
    [Required]
    public Guid TaskKey { get; init; }

    public string NodeKind { get; init; } = "PluginTask";

    public string? TaskDefinitionName { get; init; }

    public ConditionalRule? ConditionalRule { get; init; }
    public string? CollectionIteratorItemKind { get; init; }

    [Range(0, int.MaxValue)]
    public int AllowedMajor { get; init; }

    [Range(0, int.MaxValue)]
    public int AllowedMinor { get; init; }

    [Required]
    public CoordinatesViewModel Position { get; init; }

    public Dictionary<string, JsonElement> DesignTimeInputOverrides { get; init; } = [];

    public List<ExportedBlueprintConnectionInputModel> OutgoingConnections { get; init; } = [];
}
