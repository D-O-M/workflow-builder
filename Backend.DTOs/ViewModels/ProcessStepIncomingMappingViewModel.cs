namespace Backend.Models.ViewModels;

public sealed class ProcessStepIncomingMappingViewModel
{
    public required Guid SourceBlueprintNodeId { get; init; }

    public required string SourceName { get; init; }

    public string? ChannelName { get; init; }

    public List<BlueprintPropertyMappingViewModel> PropertyMappings { get; init; } = [];
}
