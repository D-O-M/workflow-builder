namespace Backend.Models.ViewModels;

public sealed class BlueprintPropertyMappingViewModel
{
    public required string SourceProperty { get; init; }

    public required string TargetProperty { get; init; }
}
