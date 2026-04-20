using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
namespace Backend.Models.InputModels;

public sealed class BlueprintInputModel
{
    /// <summary>The stable blueprint key. Optional on create; generated from Name when omitted.</summary>
    public string? Key { get; init; }

    /// <summary>The name of the blueprint.</summary>
    [Required, MinLength(1)]
    public string Name { get; init; } = null!;

    /// <summary>The initial version name to assign when creating a brand-new blueprint definition.</summary>
    public string? VersionName { get; init; }

    /// <summary>The description of the blueprint.</summary>
    public string? Description { get; init; }

    /// <summary>The task instance ID of the starting task within this payload (entry point).</summary>
    [Required]
    public Guid StartingBlueprintNodeId { get; init; }

    /// <summary>
    /// The blueprint tasks for this immutable version.
    /// NOTE: VersionId values must be unique within this array (enforce in service layer).
    /// </summary>
    [Required, MinLength(1)]
    public List<BlueprintNodeInputModel> Tasks { get; init; } = new();
}
