namespace Backend.Models.ViewModels;

public sealed class BlueprintImportViewModel
{
    public required string Status { get; init; }
    public int BlueprintDefinitionId { get; init; }
    public int BlueprintVersionId { get; init; }
    public required string BlueprintKey { get; init; }
    public string? StoredDisplayName { get; init; }
    public string? ImportedDisplayName { get; init; }
    public bool CanIgnoreImportedDisplayNameMismatch { get; init; }
}
