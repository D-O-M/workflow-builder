using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.PluginBundleBuild;

public sealed class PluginBundleBuildPlugin : IPlugin
{
    public required PluginBundleBuildInput Input { get; init; }
    public PluginBundleBuildResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var workspacePath = CiCdPaths.ResolveWorkspacePath(Input.WorkspacePath, null);
        var pluginsRoot = CiCdPaths.ResolveWorkspacePath(workspacePath, string.IsNullOrWhiteSpace(Input.PluginsRoot) ? "Backend.Plugins" : Input.PluginsRoot);
        var outputPath = CiCdPaths.ResolveWorkspacePath(workspacePath, string.IsNullOrWhiteSpace(Input.BundleOutputPath) ? "Backend.Plugins/_bundles" : Input.BundleOutputPath);
        var configuration = string.IsNullOrWhiteSpace(Input.Configuration) ? "Release" : Input.Configuration.Trim();
        var scriptPath = CiCdPaths.ResolveWorkspacePath(workspacePath, "BuildScripts/bundle-plugins.ps1");
        var logPath = CiCdPaths.GetLogPath(workspacePath, buildId, "plugin-bundle-build");

        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-RootDir",
            pluginsRoot,
            "-OutputDir",
            outputPath,
            "-Configuration",
            configuration,
            "-Overwrite"
        };
        if (!string.IsNullOrWhiteSpace(Input.RuntimeIdentifier))
        {
            arguments.Add("-RuntimeIdentifier");
            arguments.Add(Input.RuntimeIdentifier.Trim());
        }

        await CommandRunner.RunAsync(
            CommandRunner.ResolvePowerShellExecutable(),
            arguments,
            workspacePath,
            logPath);

        Result = new PluginBundleBuildResult
        {
            BuildId = buildId,
            WorkspacePath = workspacePath,
            PluginBundleOutputPath = outputPath,
            PluginBundleLogPath = logPath
        };
    }
}
