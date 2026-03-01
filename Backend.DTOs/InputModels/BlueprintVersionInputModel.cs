using System.ComponentModel.DataAnnotations;

namespace Backend.DTOs.InputModels;

public sealed class BlueprintVersionInputModel
{
    /// <summary>The ID of the blueprint definition this version belongs to.</summary>
    [Required]
    [Range(1, int.MaxValue)]
    public int BlueprintDefinitionId { get; init; }

    /// <summary>
    /// Optional: the existing blueprint version number this new version is based on (e.g. create vN+1 from vN).
    /// Omit for the first version.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? BaseVersion { get; init; }

    /// <summary>The name of the blueprint.</summary>
    [Required, MinLength(1)]
    public string Name { get; init; } = null!;

    /// <summary>The description of the blueprint.</summary>
    public string? Description { get; init; }

    /// <summary>The Task Instance ID of the starting task within this payload (entry point).</summary>
    [Required]
    public Guid StartingTaskInstanceId { get; init; }

    /// <summary>
    /// The blueprint tasks for this immutable version.
    /// NOTE: VersionId values must be unique within this array (enforce in service layer).
    /// </summary>
    [Required, MinLength(1)]
    public List<BlueprintTaskInputModel> Tasks { get; init; } = new();
}
