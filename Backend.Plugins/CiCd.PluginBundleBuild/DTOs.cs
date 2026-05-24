namespace CiCd.PluginBundleBuild;

public sealed class PluginBundleBuildInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string PluginsRoot { get; set; } = "Backend.Plugins";
    public string BundleOutputPath { get; set; } = "Backend.Plugins/_bundles";
    public string Configuration { get; set; } = "Release";
    public string RuntimeIdentifier { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class PluginBundleBuildResult
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string PluginBundleOutputPath { get; set; } = "";
    public string PluginBundleLogPath { get; set; } = "";
}
