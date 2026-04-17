namespace WorkflowBuilder.IntegrationTests.Infrastructure;

internal static class IntegrationProgressLog
{
    private static readonly object SyncLock = new();
    private static string? _path;

    public static void Initialize(string runVersion)
    {
        var liveRunsDirectory = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "LiveRuns"));

        Directory.CreateDirectory(liveRunsDirectory);

        var safeRunVersion = string.Join(
            "-",
            runVersion.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        _path = Path.Combine(liveRunsDirectory, $"activity-{safeRunVersion}.log");
        File.WriteAllText(_path, string.Empty);
    }

    public static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            return;
        }

        var line = $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}";

        lock (SyncLock)
        {
            File.AppendAllText(_path, line);
        }
    }
}
