using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class PhasedBlueprintHandoffMappingInputModel
{
    public string SourceKind { get; init; } = "Output";

    public Guid? SourceBlueprintNodeId { get; init; }

    public string? SourceChannelName { get; init; }

    [Required, MinLength(1)]
    public string SourceProperty { get; init; } = null!;

    public double BridgePosition { get; init; } = 0.5d;

    [Required]
    public Guid TargetBlueprintNodeId { get; init; }

    [Required, MinLength(1)]
    public string TargetProperty { get; init; } = null!;
}

