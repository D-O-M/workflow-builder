namespace Backend.Models.ViewModels;

public sealed class PhasedBlueprintViewModel
{
    public int Id { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public List<PhasedBlueprintVersionViewModel> Versions { get; init; } = [];
}

