using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationSeedPlugin;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class IntegrationWorkflowState : IGlobalState
{
    public string RunLabel { get; set; } = string.Empty;

    public int StepsCompleted { get; set; }

    public int TotalScore { get; set; }

    public string LastStage { get; set; } = string.Empty;

    public bool ShouldFail { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class SeedRequest
{
    public string Name { get; set; } = string.Empty;

    public int BaseScore { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class SeedSuccess
{
    public string NormalizedName { get; set; } = string.Empty;

    public int Score { get; set; }
}
