using System.Text.Json;

namespace Backend.Models.ViewModels;

public sealed class ProcessStepDetailViewModel
{
    public Guid? RuntimeStepId { get; init; }
    public List<ProcessStepAttemptViewModel> Attempts { get; init; } = [];
    public required Guid BlueprintNodeId { get; init; }
    public required string Name { get; init; }
    public string NodeKind { get; init; } = "PluginTask";
    public ConditionalRule? ConditionalRule { get; init; }
    public List<ConditionalField> ConditionalFields { get; init; } = [];
    public string? SelectedChannel { get; init; }
    public string? CollectionIteratorItemKind { get; init; }
    public int RuntimeStepCount { get; init; }
    public required string Status { get; init; }
    public string? ResolvedVersion { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public double? DurationMs { get; init; }
    public string? FailureMessage { get; init; }
    public JsonElement? Inputs { get; init; }
    public JsonElement? Outputs { get; init; }
    public List<ProcessStepIncomingMappingViewModel> IncomingMappings { get; init; } = [];
    public JsonElement? DesignTimeInputOverrides { get; init; }
    public JsonElement? RuntimeOverrides { get; init; }
    public List<ProcessStepIterationViewModel> Iterations { get; init; } = [];
}

public sealed record ProcessStepAttemptViewModel(string RuntimeStepId, string LogicalStepId, string Status,
    DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? ResolvedVersion, Guid? RecoveryId,
    string? RecoveryAction, string? ReusedFromStepId, string? FailureMessage);

public sealed class ProcessStepIterationViewModel
{
    public required Guid RuntimeStepId { get; init; }
    public required string SourceIteratorStepId { get; init; }
    public required string SourceIteratorBlueprintNodeId { get; init; }
    public required int ItemIndex { get; init; }
    public string? SelectedChannel { get; init; }
    public required string ItemKey { get; init; }
    public required string Status { get; init; }
}
