namespace Microsoft.Extensions.Hosting;

public sealed class WorkflowAzureAppConfigurationOptions
{
    public const string SectionName = "AzureAppConfiguration";

    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string ManagedIdentityClientId { get; set; } = string.Empty;

    public bool IncludeNullLabel { get; set; } = true;

    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(Endpoint);
}
