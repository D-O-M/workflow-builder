namespace CiCd.DotNetTest;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DotNetTestInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string TestProjectList { get; set; } = "Backend.API.Tests/Backend.API.Tests.csproj;Backend.Engine.Tests/Backend.Engine.Tests.csproj;Backend.Plugins.Harness.Tests/Backend.Plugins.Harness.Tests.csproj;Backend.ShowcaseTown.Tests/Backend.ShowcaseTown.Tests.csproj";
    public string Configuration { get; set; } = "Release";
    public bool NoBuild { get; set; } = true;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DotNetTestResult
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string TestProjectList { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string DotNetTestLogPath { get; set; } = "";
}
