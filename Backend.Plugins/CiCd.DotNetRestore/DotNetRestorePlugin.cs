using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.DotNetRestore;

public sealed class DotNetRestorePlugin : IPlugin
{
    public required DotNetRestoreInput Input { get; init; }
    public DotNetRestoreResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var workspacePath = CiCdPaths.ResolveWorkspacePath(Input.WorkspacePath, null);
        var solutionPath = CiCdPaths.ResolveWorkspacePath(workspacePath, string.IsNullOrWhiteSpace(Input.SolutionPath) ? "Backend.slnx" : Input.SolutionPath);
        var logPath = CiCdPaths.GetLogPath(workspacePath, buildId, "dotnet-restore");

        await CommandRunner.RunAsync(
            "dotnet",
            ["restore", solutionPath],
            workspacePath,
            logPath);

        Result = new DotNetRestoreResult
        {
            BuildId = buildId,
            WorkspacePath = workspacePath,
            SolutionPath = solutionPath,
            RestoreLogPath = logPath
        };
    }
}
