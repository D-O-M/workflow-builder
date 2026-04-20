namespace Backend.Models.ViewModels;

public sealed class BlueprintVersionViewModel
{
    /// <summary>Server-generated ID for the blueprint version row (BlueprintVersions.Id).</summary>
    public int BlueprintVersionId { get; init; }

    /// <summary>Owning blueprint definition (BlueprintDefinitions.Id).</summary>
    public int BlueprintDefinitionId { get; init; }

    public string? Description { get; init; }

    /// <summary>Monotonic version number for the blueprint definition (BlueprintVersions.Version).</summary>
    public int Version { get; init; }

    /// <summary>The task instance ID of the starting task within this payload (entry point).</summary>
    public Guid StartingBlueprintNodeId { get; init; }

    public List<BlueprintNodeViewModel> Tasks { get; init; } = new();
}
