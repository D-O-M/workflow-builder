using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public sealed class BlueprintPropertyMappingInputModel
{
    [Required, MinLength(1)]
    public string SourceProperty { get; init; } = null!;

    [Required, MinLength(1)]
    public string TargetProperty { get; init; } = null!;
}
