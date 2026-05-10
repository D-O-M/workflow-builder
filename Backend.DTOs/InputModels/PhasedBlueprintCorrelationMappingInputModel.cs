using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class PhasedBlueprintCorrelationMappingInputModel
{
    [Required, MinLength(1)]
    public string SourceResultProperty { get; init; } = null!;

    [Required, MinLength(1)]
    public string TriggerProperty { get; init; } = null!;
}

