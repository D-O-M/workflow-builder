using System.Text.RegularExpressions;

namespace CiCd.Common;

public static partial class CiCdPaths
{
    public static string ResolveWorkspacePath(string workspacePath, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Path.GetFullPath(workspacePath);
        }

        return Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(workspacePath, path));
    }

    public static string ResolveWorkRoot(string? configuredRoot) =>
        Path.GetFullPath(
            !string.IsNullOrWhiteSpace(configuredRoot)
                ? configuredRoot
                : Environment.GetEnvironmentVariable("WORKFLOW_BUILDER_CICD_WORKROOT")
                    ?? Path.Combine(Path.GetTempPath(), "workflow-builder-cicd"));

    public static string ResolveRepositoryUrl(string? configuredRepositoryUrl)
    {
        var repositoryUrl = configuredRepositoryUrl;
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            repositoryUrl =
                Environment.GetEnvironmentVariable("WORKFLOW_BUILDER_DEMO_REPOSITORY")
                ?? Environment.GetEnvironmentVariable("WorkflowBuilder__RepositoryRoot");
        }

        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            throw new InvalidDataException("RepositoryUrl is required when WORKFLOW_BUILDER_DEMO_REPOSITORY is not configured.");
        }

        return repositoryUrl;
    }

    public static string ResolveBuildId(string? configuredBuildId) =>
        string.IsNullOrWhiteSpace(configuredBuildId)
            ? $"build-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32]
            : configuredBuildId.Trim();

    public static string SafeBuildId(string buildId)
    {
        var safe = UnsafePathCharactersRegex().Replace(buildId.Trim(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "build" : safe;
    }

    public static string GetLogPath(string workspacePath, string buildId, string stepName)
    {
        var logDirectory = Path.Combine(workspacePath, ".artifacts", "cicd", SafeBuildId(buildId));
        Directory.CreateDirectory(logDirectory);
        return Path.Combine(logDirectory, $"{stepName}.log");
    }

    public static string GetScratchPath(string buildId, string stepName)
    {
        var scratchPath = Path.Combine(Path.GetTempPath(), "workflow-builder-cicd", SafeBuildId(buildId), stepName);
        Directory.CreateDirectory(scratchPath);
        return scratchPath;
    }

    public static string[] SplitPathList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray();

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafePathCharactersRegex();
}
