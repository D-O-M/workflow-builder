using System.ComponentModel.DataAnnotations;
using Backend.Models.ViewModels;

namespace Backend.Models.InputModels;

public sealed class ExportedBlueprintNodeInputModel
{
    [Required]
    public Guid TaskKey { get; init; }

    [Required, MinLength(1)]
    public string TaskDefinitionName { get; init; } = null!;

    [Range(0, int.MaxValue)]
    public int AllowedMajor { get; init; }

    [Range(0, int.MaxValue)]
    public int AllowedMinor { get; init; }

    [Required]
    public CoordinatesViewModel Position { get; init; }

    public List<ExportedBlueprintConnectionInputModel> OutgoingConnections { get; init; } = [];
}
