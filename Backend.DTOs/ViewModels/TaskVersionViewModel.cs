namespace Backend.Models.ViewModels;

public record TaskDefinitionViewModel
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public List<TaskVersionViewModel> Versions { get; init; } = new();
}

public record TaskVersionViewModel
{
    public int Id { get; init; }
    public required string Version { get; init; }
    public required string State { get; init; }
    public string? Description { get; init; }
    public required TaskChannel? RequiredGlobalState { get; init; }
    public TaskChannel? InputChannel { get; init; }
    public required List<TaskChannel> OutputChannels { get; init; } = new();
}

public record TaskChannel
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required Dictionary<string, string> Properties { get; init; } = new();
} 
