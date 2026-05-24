namespace CiCd.SendPhaseTriggerEmail;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class SendPhaseTriggerEmailInput
{
    public string BuildId { get; set; } = "";
    public string ArtifactContainerName { get; set; } = "";
    public string ArtifactPackageBlobName { get; set; } = "";
    public string ArtifactManifestBlobName { get; set; } = "";
    public string ArtifactPackageUri { get; set; } = "";
    public string ArtifactManifestUri { get; set; } = "";
    public string ApprovalEmailAddress { get; set; } = "";
    public string PhaseSeriesKey { get; set; } = "cicd-phase-series-demo";
    public int TargetPhaseIndex { get; set; } = 1;
    public string TriggerBaseUrl { get; set; } = "";
    public string OutboxRoot { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class SendPhaseTriggerEmailResult
{
    public string BuildId { get; set; } = "";
    public string ApprovalLink { get; set; } = "";
    public string OutboxMessagePath { get; set; } = "";
    public string ArtifactContainerName { get; set; } = "";
    public string ArtifactPackageBlobName { get; set; } = "";
    public string ArtifactManifestBlobName { get; set; } = "";
    public string ArtifactPackageUri { get; set; } = "";
    public string ArtifactManifestUri { get; set; } = "";
}
