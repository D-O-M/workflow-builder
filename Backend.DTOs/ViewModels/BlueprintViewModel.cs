namespace Backend.Models.ViewModels;

public class BlueprintViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<BlueprintVersionViewModel> Versions { get; init; } = [];
}
