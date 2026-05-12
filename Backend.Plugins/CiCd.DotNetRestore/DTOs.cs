namespace CiCd.DotNetRestore;

public sealed class DotNetRestoreInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string SolutionPath { get; set; } = "Backend.slnx";
}

public sealed class DotNetRestoreResult
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string SolutionPath { get; set; } = "";
    public string RestoreLogPath { get; set; } = "";
}
