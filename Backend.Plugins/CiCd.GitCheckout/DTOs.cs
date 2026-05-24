namespace CiCd.GitCheckout;

public sealed class GitCheckoutInput
{
    public string BuildId { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string RepositoryRef { get; set; } = "HEAD";
    public string WorkRoot { get; set; } = "";
    public bool CleanWorkspace { get; set; } = true;
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class GitCheckoutResult
{
    public string BuildId { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string RepositoryRef { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string CommitSha { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public string CheckoutLogPath { get; set; } = "";
}
