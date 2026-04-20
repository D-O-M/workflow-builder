using System.Diagnostics;
using Shouldly;

namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal sealed class PluginBundleBuilder
{
    private readonly string _repoRoot;

    public PluginBundleBuilder(string repoRoot)
    {
        _repoRoot = repoRoot;
    }

    public async Task<PluginBundleCatalog> BuildAsync(CancellationToken cancellationToken)
    {
        var outputDir = Path.Combine(
            Path.GetTempPath(),
            "workflow-builder-integration-bundles",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outputDir);

        var scriptPath = Path.Combine(
            _repoRoot,
            "IntegrationTests",
            "scripts",
            "build-integration-plugin-bundles.ps1");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-File \"{scriptPath}\" -OutputDir \"{outputDir}\"",
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.Start();

        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        process.ExitCode.ShouldBe(
            0,
            $"Plugin bundle build failed.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");

        var bundlePathsByName = Directory
            .EnumerateFiles(outputDir, "*.zip", SearchOption.TopDirectoryOnly)
            .ToDictionary(GetPluginNameFromBundlePath, path => path, StringComparer.Ordinal);

        bundlePathsByName.Keys.ShouldContain(PluginBundleCatalog.SeedPlugin);
        bundlePathsByName.Keys.ShouldContain(PluginBundleCatalog.TransformPlugin);
        bundlePathsByName.Keys.ShouldContain(PluginBundleCatalog.ResultPlugin);
        bundlePathsByName.Keys.ShouldContain(PluginBundleCatalog.FailurePlugin);

        return new PluginBundleCatalog(bundlePathsByName);
    }

    private static string GetPluginNameFromBundlePath(string bundlePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(bundlePath);
        var versionSeparator = fileName.LastIndexOf('_');
        return versionSeparator > 0 ? fileName[..versionSeparator] : fileName;
    }
}
