using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.DotNetBuild;

public sealed class DotNetBuildPlugin : IPlugin
{
    public required DotNetBuildInput Input { get; init; }
    public DotNetBuildResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var workspacePath = CiCdPaths.ResolveWorkspacePath(Input.WorkspacePath, null);
        var solutionPath = CiCdPaths.ResolveWorkspacePath(workspacePath, string.IsNullOrWhiteSpace(Input.SolutionPath) ? "Backend.slnx" : Input.SolutionPath);
        var configuration = string.IsNullOrWhiteSpace(Input.Configuration) ? "Release" : Input.Configuration.Trim();
        var logPath = CiCdPaths.GetLogPath(workspacePath, buildId, "dotnet-build");

        await CommandRunner.RunAsync(
            "dotnet",
            ["build", solutionPath, "--configuration", configuration, "--no-restore"],
            workspacePath,
            logPath);

        Result = new DotNetBuildResult
        {
            BuildId = buildId,
            WorkspacePath = workspacePath,
            SolutionPath = solutionPath,
            Configuration = configuration,
            BuildLogPath = logPath
        };
    }
}
