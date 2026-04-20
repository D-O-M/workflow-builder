using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class ExportedBlueprintInputModel
{
    [Range(1, int.MaxValue)]
    public int SchemaVersion { get; init; }

    [Required, MinLength(1)]
    public string DefinitionKey { get; init; } = null!;

    [Required, MinLength(1)]
    public string DisplayName { get; init; } = null!;

    [Required, MinLength(1)]
    public string VersionName { get; init; } = null!;

    [Required]
    public Guid StartingTaskKey { get; init; }

    [Required, MinLength(1)]
    public List<ExportedBlueprintTaskInputModel> Tasks { get; init; } = [];
}
