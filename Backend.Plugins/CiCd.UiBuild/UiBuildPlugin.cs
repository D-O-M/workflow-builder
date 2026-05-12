using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.UiBuild;

public sealed class UiBuildPlugin : IPlugin
{
    public required UiBuildInput Input { get; init; }
    public UiBuildResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var workspacePath = CiCdPaths.ResolveWorkspacePath(Input.WorkspacePath, null);
        var uiPath = CiCdPaths.ResolveWorkspacePath(workspacePath, string.IsNullOrWhiteSpace(Input.UiPath) ? "UI" : Input.UiPath);
        var logPath = CiCdPaths.GetLogPath(workspacePath, buildId, "ui-build");

        await CommandRunner.RunAsync(
            CommandRunner.ResolveExecutable("npm"),
            ["run", "build"],
            uiPath,
            logPath);

        Result = new UiBuildResult
        {
            BuildId = buildId,
            WorkspacePath = workspacePath,
            UiPath = uiPath,
            UiDistPath = Path.Combine(uiPath, "dist"),
            UiBuildLogPath = logPath
        };
    }
}
