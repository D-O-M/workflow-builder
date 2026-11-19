using System.Text.Json.Serialization;

namespace Backend.Database.State.Objects;

public sealed class ProcessRecoveryState
{
    public Guid RecoveryId { get; set; }
    public int Revision { get; set; }
    public long Sequence { get; set; }
    public string Outcome { get; set; } = "Running";
    public string? Detail { get; set; }
    public Dictionary<string, string> EffectiveAttemptIds { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public bool Active => Outcome is "Running" or "Finalizing";

    public static IReadOnlyList<ProcessStepRecord> EffectiveSteps(
        ProcessInstanceRecord process, IEnumerable<ProcessStepRecord> steps)
    {
        var map = process.Recovery?.EffectiveAttemptIds;
        return steps.Where(step => map is not null && map.TryGetValue(step.LogicalStepId ?? step.Id, out var current)
                ? step.Id == current
                : step.RecoveryId is null)
            .ToList();
    }
}
