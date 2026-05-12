namespace CiCd.UiBuild;

public sealed class UiBuildInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string UiPath { get; set; } = "UI";
}

public sealed class UiBuildResult
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string UiPath { get; set; } = "";
    public string UiDistPath { get; set; } = "";
    public string UiBuildLogPath { get; set; } = "";
}
