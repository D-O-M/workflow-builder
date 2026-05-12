using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.UiDependencyInstall;

public sealed class UiDependencyInstallPlugin : IPlugin
{
    public required UiDependencyInstallInput Input { get; init; }
    public UiDependencyInstallResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var workspacePath = CiCdPaths.ResolveWorkspacePath(Input.WorkspacePath, null);
        var uiPath = CiCdPaths.ResolveWorkspacePath(workspacePath, string.IsNullOrWhiteSpace(Input.UiPath) ? "UI" : Input.UiPath);
        var logPath = CiCdPaths.GetLogPath(workspacePath, buildId, "ui-install");
        var installVerb = File.Exists(Path.Combine(uiPath, "package-lock.json")) ? "ci" : "install";

        await CommandRunner.RunAsync(
            CommandRunner.ResolveExecutable("npm"),
            [installVerb],
            uiPath,
            logPath);

        Result = new UiDependencyInstallResult
        {
            BuildId = buildId,
            WorkspacePath = workspacePath,
            UiPath = uiPath,
            PackageManager = "npm",
            InstallLogPath = logPath
        };
    }
}
