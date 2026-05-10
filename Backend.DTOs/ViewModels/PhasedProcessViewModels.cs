using System.Text.Json;

namespace Backend.Models.ViewModels;

public sealed class PhasedProcessAcceptedViewModel
{
    public required Guid PhasedProcessId { get; init; }

    public required int PhasedBlueprintDefinitionId { get; init; }

    public required int PhasedBlueprintVersionId { get; init; }

    public required Guid ChildProcessId { get; init; }

    public required int PhaseIndex { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }
}

public sealed class PhasedProcessDetailViewModel
{
    public required Guid PhasedProcessId { get; init; }

    public required int PhasedBlueprintDefinitionId { get; init; }

    public required string PhasedBlueprintName { get; init; }

    public required int PhasedBlueprintVersionId { get; init; }

    public required int PhasedBlueprintVersionNumber { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public List<PhasedProcessPhaseViewModel> Phases { get; init; } = [];
}

public sealed class PhasedProcessPhaseViewModel
{
    public required Guid PhaseId { get; init; }

    public required int PhaseIndex { get; init; }

    public required string PhaseName { get; init; }

    public required int BlueprintDefinitionId { get; init; }

    public required string BlueprintName { get; init; }

    public int? BlueprintVersionId { get; init; }

    public Guid? ChildProcessId { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? FailureMessage { get; init; }

    public JsonElement? PhaseResult { get; init; }

    public ProcessDetailViewModel? ChildProcess { get; init; }
}

