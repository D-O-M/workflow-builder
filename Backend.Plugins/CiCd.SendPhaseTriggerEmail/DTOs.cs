namespace CiCd.SendPhaseTriggerEmail;

public sealed class SendPhaseTriggerEmailInput
{
    public string BuildId { get; set; } = "";
    public string ArtifactContainerName { get; set; } = "";
    public string ArtifactPackageBlobName { get; set; } = "";
    public string ArtifactManifestBlobName { get; set; } = "";
    public string ArtifactPackageUri { get; set; } = "";
    public string ArtifactManifestUri { get; set; } = "";
    public string ApprovalEmailAddress { get; set; } = "release.approver@example.invalid";
    public string PhaseSeriesKey { get; set; } = "cicd-phase-series-demo";
    public int TargetPhaseIndex { get; set; } = 1;
    public string TriggerBaseUrl { get; set; } = "http://localhost:5180";
    public string OutboxRoot { get; set; } = "";
}

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
