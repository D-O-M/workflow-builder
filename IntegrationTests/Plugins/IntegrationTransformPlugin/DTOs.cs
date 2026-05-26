using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationTransformPlugin;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class TransformRequest
{
    public string NormalizedName { get; set; } = string.Empty;

    public int Score { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class TransformSuccess
{
    public string Message { get; set; } = string.Empty;

    public int FinalScore { get; set; }
}
