namespace Backend.Models.ViewModels;

public sealed class ExportedBlueprintViewModel
{
    public int SchemaVersion { get; init; }
    public required string DefinitionKey { get; init; }
    public required string DisplayName { get; init; }
    public required Guid StartingTaskKey { get; init; }
    public List<ExportedBlueprintNodeViewModel> Tasks { get; init; } = [];
}
