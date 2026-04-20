using System.Text.Json;

namespace Backend.Models.ViewModels;

public sealed class ProcessStepDetailViewModel
{
    public required Guid BlueprintNodeId { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string? ResolvedVersion { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public double? DurationMs { get; init; }
    public string? FailureMessage { get; init; }
    public JsonElement? Inputs { get; init; }
    public JsonElement? Outputs { get; init; }
}
