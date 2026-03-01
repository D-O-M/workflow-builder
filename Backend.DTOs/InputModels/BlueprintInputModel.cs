using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Backend.DTOs.InputModels;

public sealed class BlueprintInputModel
{
    /// <summary>The name of the blueprint.</summary>
    [Required, MinLength(1)]
    public string Name { get; init; } = null!;

    /// <summary>The description of the blueprint.</summary>
    public string? Description { get; init; }

    /// <summary>The Task Instance ID of the starting task within this payload (entry point).</summary>
    [Required, Range(1, int.MaxValue)]
    public int StartingTaskInstanceId { get; init; }

    /// <summary>
    /// The blueprint tasks for this immutable version.
    /// NOTE: VersionId values must be unique within this array (enforce in service layer).
    /// </summary>
    [Required, MinLength(1)]
    public List<BlueprintTaskInputModel> Tasks { get; init; } = new();
}
