using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationResultPlugin;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class ResultRequest
{
    public string Message { get; set; } = string.Empty;

    public int FinalScore { get; set; }
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class ResultSuccess
{
    public string Summary { get; set; } = string.Empty;

    public int FinalScore { get; set; }
}
