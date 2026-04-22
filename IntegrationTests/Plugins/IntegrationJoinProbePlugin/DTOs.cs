using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationJoinProbePlugin;

public sealed class IntegrationWorkflowState : IGlobalState
{
    public string RunLabel { get; set; } = string.Empty;

    public int StepsCompleted { get; set; }

    public int TotalScore { get; set; }

    public string LastStage { get; set; } = string.Empty;

    public bool ShouldFail { get; set; }
}

public sealed class JoinProbeRequest
{
    public string LeftText { get; set; } = string.Empty;

    public string RightText { get; set; } = string.Empty;

    public int CombinedScore { get; set; }

    public string Mode { get; set; } = string.Empty;
}

public sealed class JoinProbeSuccess
{
    public string Summary { get; set; } = string.Empty;

    public int CombinedScore { get; set; }

    public string Mode { get; set; } = string.Empty;
}
