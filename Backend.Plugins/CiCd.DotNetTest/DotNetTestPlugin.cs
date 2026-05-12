using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.DotNetTest;

public sealed class DotNetTestPlugin : IPlugin
{
    public required DotNetTestInput Input { get; init; }
    public DotNetTestResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var workspacePath = CiCdPaths.ResolveWorkspacePath(Input.WorkspacePath, null);
        var configuration = string.IsNullOrWhiteSpace(Input.Configuration) ? "Release" : Input.Configuration.Trim();
        var testProjects = CiCdPaths.SplitPathList(Input.TestProjectList);
        if (testProjects.Length == 0)
        {
            throw new InvalidDataException("TestProjectList must include at least one .NET test project path.");
        }

        var logPath = CiCdPaths.GetLogPath(workspacePath, buildId, "dotnet-tests");
        foreach (var testProject in testProjects)
        {
            var arguments = new List<string>
            {
                "test",
                CiCdPaths.ResolveWorkspacePath(workspacePath, testProject),
                "--configuration",
                configuration
            };
            if (Input.NoBuild)
            {
                arguments.Add("--no-build");
            }

            await CommandRunner.RunAsync(
                "dotnet",
                arguments,
                workspacePath,
                logPath);
        }

        Result = new DotNetTestResult
        {
            BuildId = buildId,
            WorkspacePath = workspacePath,
            TestProjectList = string.Join(';', testProjects),
            Configuration = configuration,
            DotNetTestLogPath = logPath
        };
    }
}
