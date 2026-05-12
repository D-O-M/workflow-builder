using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.UiUnitTest;

public sealed class UiUnitTestPlugin : IPlugin
{
    public required UiUnitTestInput Input { get; init; }
    public UiUnitTestResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var workspacePath = CiCdPaths.ResolveWorkspacePath(Input.WorkspacePath, null);
        var uiPath = CiCdPaths.ResolveWorkspacePath(workspacePath, string.IsNullOrWhiteSpace(Input.UiPath) ? "UI" : Input.UiPath);
        var logPath = CiCdPaths.GetLogPath(workspacePath, buildId, "ui-unit-tests");

        await CommandRunner.RunAsync(
            CommandRunner.ResolveExecutable("npm"),
            ["run", "test:unit"],
            uiPath,
            logPath);

        Result = new UiUnitTestResult
        {
            BuildId = buildId,
            WorkspacePath = workspacePath,
            UiPath = uiPath,
            UiTestLogPath = logPath
        };
    }
}
