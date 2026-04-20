namespace Backend.Models.ViewModels;

public sealed class ProcessDetailViewModel
{
    public required Guid ProcessId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required string BlueprintName { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int BlueprintVersionNumber { get; init; }
    public required string BlueprintVersionName { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public required Guid StartingBlueprintNodeId { get; init; }
    public List<ProcessStepViewModel> Nodes { get; init; } = [];
}
