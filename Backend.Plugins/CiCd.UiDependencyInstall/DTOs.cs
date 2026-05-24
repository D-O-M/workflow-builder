namespace CiCd.UiDependencyInstall;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class UiDependencyInstallInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string UiPath { get; set; } = "UI";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class UiDependencyInstallResult
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string UiPath { get; set; } = "";
    public string PackageManager { get; set; } = "";
    public string InstallLogPath { get; set; } = "";
}
