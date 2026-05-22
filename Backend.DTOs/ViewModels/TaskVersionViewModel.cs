namespace Backend.Models.ViewModels;

public record TaskDefinitionViewModel
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public List<TaskVersionViewModel> Versions { get; init; } = new();
}

public record TaskVersionViewModel
{
    public int Id { get; init; }
    public required string Version { get; init; }
    public required string State { get; init; }
    public string? Description { get; init; }
    public TaskVersionRuntimeAvailabilityViewModel RuntimeAvailability { get; init; } = new();
    public required TaskChannel? RequiredGlobalState { get; init; }
    public TaskChannel? InputChannel { get; init; }
    public required List<TaskChannel> OutputChannels { get; init; } = new();
}

public record TaskVersionRuntimeAvailabilityViewModel
{
    public string Status { get; init; } = "UnknownNotLoaded";
    public bool Runnable { get; init; }
    public int ReportingEngineCount { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }
    public string? Message { get; init; }
    public List<TaskVersionRuntimeIssueViewModel> Issues { get; init; } = [];
}

public record TaskVersionRuntimeIssueViewModel
{
    public string? CapabilityId { get; init; }
    public string? DisplayName { get; init; }
    public string? Kind { get; init; }
    public string? Status { get; init; }
    public string? Category { get; init; }
    public string? Message { get; init; }
    public string? Remediation { get; init; }
}

public record TaskChannel
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required List<TaskPropertyViewModel> Properties { get; init; } = [];
}

public record TaskPropertyViewModel
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required bool Required { get; init; }
    public required List<string> EnumValues { get; init; } = [];
}
