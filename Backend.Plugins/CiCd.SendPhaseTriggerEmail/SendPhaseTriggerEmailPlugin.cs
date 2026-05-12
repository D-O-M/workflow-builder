using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.SendPhaseTriggerEmail;

public sealed class SendPhaseTriggerEmailPlugin : IPlugin
{
    public required SendPhaseTriggerEmailInput Input { get; init; }
    public SendPhaseTriggerEmailResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var baseUrl = string.IsNullOrWhiteSpace(Input.TriggerBaseUrl)
            ? "http://localhost:5180"
            : Input.TriggerBaseUrl.Trim().TrimEnd('/');
        var targetPhaseIndex = Input.TargetPhaseIndex <= 0 ? 1 : Input.TargetPhaseIndex;
        var phaseSeriesKey = string.IsNullOrWhiteSpace(Input.PhaseSeriesKey)
            ? "cicd-phase-series-demo"
            : Input.PhaseSeriesKey.Trim();
        var approvalLink = BuildApprovalLink(baseUrl, phaseSeriesKey, targetPhaseIndex, buildId);
        var outboxRoot = string.IsNullOrWhiteSpace(Input.OutboxRoot)
            ? Environment.GetEnvironmentVariable("WORKFLOW_BUILDER_DEV_OUTBOX")
                ?? Path.Combine(Path.GetTempPath(), "workflow-builder-outbox")
            : Input.OutboxRoot.Trim();
        Directory.CreateDirectory(outboxRoot);

        var messagePath = Path.Combine(
            outboxRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{CiCdPaths.SafeBuildId(buildId)}-approval.eml");
        await File.WriteAllTextAsync(messagePath, CreateEmailBody(buildId, approvalLink));

        Result = new SendPhaseTriggerEmailResult
        {
            BuildId = buildId,
            ApprovalLink = approvalLink,
            OutboxMessagePath = messagePath,
            ArtifactContainerName = Input.ArtifactContainerName,
            ArtifactPackageBlobName = Input.ArtifactPackageBlobName,
            ArtifactManifestBlobName = Input.ArtifactManifestBlobName,
            ArtifactPackageUri = Input.ArtifactPackageUri,
            ArtifactManifestUri = Input.ArtifactManifestUri
        };
    }

    private static string BuildApprovalLink(
        string baseUrl,
        string phaseSeriesKey,
        int targetPhaseIndex,
        string buildId) =>
        $"{baseUrl}/api/phase-trigger-links/activate?seriesKey={Uri.EscapeDataString(phaseSeriesKey)}&targetPhaseIndex={targetPhaseIndex}&BuildId={Uri.EscapeDataString(buildId)}";

    private string CreateEmailBody(string buildId, string approvalLink) =>
        $"""
        To: {Input.ApprovalEmailAddress}
        From: workflow-builder@example.invalid
        Subject: Approve CI/CD deployment for {buildId}

        Build {buildId} completed and published artifacts.

        Artifact manifest:
        {Input.ArtifactManifestUri}

        Artifact package:
        {Input.ArtifactPackageUri}

        Trigger deployment:
        {approvalLink}
        """;
}
