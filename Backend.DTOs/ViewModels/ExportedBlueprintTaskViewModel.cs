namespace Backend.Models.ViewModels;

public sealed class ExportedBlueprintTaskViewModel
{
    public required Guid TaskKey { get; init; }
    public required string TaskDefinitionName { get; init; }
    public int AllowedMajor { get; init; }
    public int AllowedMinor { get; init; }
    public required CoordinatesViewModel Position { get; init; }
    public List<ExportedBlueprintConnectionViewModel> OutgoingConnections { get; init; } = [];
}
