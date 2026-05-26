using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationFailurePlugin;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class FailureRequest
{
    public string NormalizedName { get; set; } = string.Empty;

    public int Score { get; set; }

    public bool ShouldFail { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class FailureSuccess
{
    public string Outcome { get; set; } = string.Empty;
}
