using Backend.Plugins.Harness;

namespace WorkflowBuilder.IntegrationTests.Plugins.IntegrationJoinProbePlugin;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class JoinProbeRequest
{
    public string LeftText { get; set; } = string.Empty;

    public string RightText { get; set; } = string.Empty;

    public int CombinedScore { get; set; }

    public string Mode { get; set; } = string.Empty;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class JoinProbeSuccess
{
    public string Summary { get; set; } = string.Empty;

    public int CombinedScore { get; set; }

    public string Mode { get; set; } = string.Empty;
}
