using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.GitCheckout;

public sealed class GitCheckoutPlugin : IPlugin
{
    public required GitCheckoutInput Input { get; init; }
    public GitCheckoutResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var repositoryUrl = CiCdPaths.ResolveRepositoryUrl(Input.RepositoryUrl);
        var workRoot = CiCdPaths.ResolveWorkRoot(Input.WorkRoot);
        var safeBuildId = CiCdPaths.SafeBuildId(buildId);
        var workspacePath = Path.Combine(workRoot, safeBuildId, "repo");
        var logDirectory = Path.Combine(workRoot, safeBuildId, ".artifacts", "cicd", safeBuildId);
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "git-checkout.log");

        if (Input.CleanWorkspace && Directory.Exists(workspacePath))
        {
            Directory.Delete(workspacePath, recursive: true);
        }

        if (!Directory.Exists(Path.Combine(workspacePath, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
            await CommandRunner.RunAsync(
                "git",
                ["clone", repositoryUrl, workspacePath],
                Path.GetDirectoryName(workspacePath)!,
                logPath);
        }

        await CommandRunner.RunAsync(
            "git",
            ["fetch", "--all", "--prune"],
            workspacePath,
            logPath);
        await CommandRunner.RunAsync(
            "git",
            ["checkout", string.IsNullOrWhiteSpace(Input.RepositoryRef) ? "HEAD" : Input.RepositoryRef.Trim()],
            workspacePath,
            logPath);

        var commitSha = await CommandRunner.CaptureAsync("git", ["rev-parse", "HEAD"], workspacePath);

        Result = new GitCheckoutResult
        {
            BuildId = buildId,
            RepositoryUrl = repositoryUrl,
            RepositoryRef = string.IsNullOrWhiteSpace(Input.RepositoryRef) ? "HEAD" : Input.RepositoryRef.Trim(),
            WorkspacePath = workspacePath,
            CommitSha = commitSha,
            RepositoryName = ResolveRepositoryName(repositoryUrl),
            CheckoutLogPath = logPath
        };
    }

    private static string ResolveRepositoryName(string repositoryUrl)
    {
        var trimmed = repositoryUrl.Trim().TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name)
            ? "repository"
            : name.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? name[..^4]
                : name;
    }
}
