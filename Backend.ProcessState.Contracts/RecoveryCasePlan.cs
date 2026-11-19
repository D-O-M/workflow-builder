using System.Text.Json;

namespace Backend.Database.State.Objects;

/// <summary>The reviewed, version-specific recovery instructions for one Process.</summary>
public sealed class RecoveryCasePlan
{
    public Guid ProcessId { get; set; }
    public int BlueprintVersionId { get; set; }
    public string? ProcessETag { get; set; }
    public List<Guid> TargetStepIds { get; set; } = [];
    public List<Guid> ContinuationStepIds { get; set; } = [];
    public string NewWorkPolicy { get; set; } = "Pause";
    public List<RecoveryPlanStep> Steps { get; set; } = [];
    public List<string> Issues { get; set; } = [];
}

public sealed class RecoveryPlanStep
{
    public Guid StepId { get; set; }
    public Guid LogicalStepId { get; set; }
    public Guid BlueprintNodeId { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string? RecoveryAction { get; set; }
    public string? ReusedFromStepId { get; set; }
    public string Action { get; set; } = "Reuse";
    public bool ActionExplicit { get; set; }
    public Guid? ReuseStepId { get; set; }
    public List<RecoveryRecordedOutcome> History { get; set; } = [];
    public bool InScope { get; set; }
    public bool CanStartHere { get; set; }
    public string? ItemKey { get; set; }
    public Dictionary<string, JsonElement>? Inputs { get; set; }
    public Dictionary<string, JsonElement>? Outputs { get; set; }
    public List<string> MissingInputs { get; set; } = [];
}

public sealed record RecoveryRecordedOutcome(Guid StepId, string Status,
    Dictionary<string, JsonElement>? Inputs, Dictionary<string, JsonElement>? Outputs,
    string? RecoveryAction = null, string? ReusedFromStepId = null);
