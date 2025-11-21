namespace Backend.Engine.Debugging;

public class DebuggerAutoAttachOptions
{
    public bool Enabled { get; set; }

    public List<string> SolutionPaths { get; set; } = new();

    public int PollingIntervalMilliseconds { get; set; } = 500;

    public int TimeoutMilliseconds { get; set; } = 5000;

    public bool LaunchIfMissing { get; set; }

    public List<string> LaunchSolutionPaths { get; set; } = new();
}
