namespace Backend.Models.ViewModels;

public class BlueprintViewModel
{
    public int Id { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public List<BlueprintVersionViewModel> Versions { get; init; }
}
