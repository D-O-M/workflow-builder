namespace CiCd.UiUnitTest;

public sealed class UiUnitTestInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string UiPath { get; set; } = "UI";
}

public sealed class UiUnitTestResult
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string UiPath { get; set; } = "";
    public string UiTestLogPath { get; set; } = "";
}
