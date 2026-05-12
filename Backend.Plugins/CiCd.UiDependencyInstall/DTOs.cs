namespace CiCd.UiDependencyInstall;

public sealed class UiDependencyInstallInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string UiPath { get; set; } = "UI";
}

public sealed class UiDependencyInstallResult
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string UiPath { get; set; } = "";
    public string PackageManager { get; set; } = "";
    public string InstallLogPath { get; set; } = "";
}
