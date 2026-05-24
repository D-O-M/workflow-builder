namespace CiCd.DotNetBuild;

public sealed class DotNetBuildInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string SolutionPath { get; set; } = "Backend.slnx";
    public string Configuration { get; set; } = "Release";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DotNetBuildResult
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string SolutionPath { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string BuildLogPath { get; set; } = "";
}
