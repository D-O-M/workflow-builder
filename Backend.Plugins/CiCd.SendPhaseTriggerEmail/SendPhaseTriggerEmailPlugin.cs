using Backend.Plugins.Configuration.CiCd;
using Backend.Plugins.Harness;
using CiCd.Common;
using Microsoft.Extensions.Options;

namespace CiCd.SendPhaseTriggerEmail;

public sealed class SendPhaseTriggerEmailPlugin : IPlugin
{
    private readonly PhaseTriggerEmailOptions _options;

    public SendPhaseTriggerEmailPlugin()
        : this(Options.Create(new PhaseTriggerEmailOptions()))
    {
    }

    public SendPhaseTriggerEmailPlugin(IOptions<PhaseTriggerEmailOptions> options)
    {
        _options = options.Value;
    }

    public required SendPhaseTriggerEmailInput Input { get; init; }
    public SendPhaseTriggerEmailResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var baseUrl = ResolveTriggerBaseUrl();
        var targetPhaseIndex = Input.TargetPhaseIndex <= 0 ? 1 : Input.TargetPhaseIndex;
        var phaseSeriesKey = string.IsNullOrWhiteSpace(Input.PhaseSeriesKey)
            ? "cicd-phase-series-demo"
            : Input.PhaseSeriesKey.Trim();
        var approvalLink = BuildApprovalLink(baseUrl, phaseSeriesKey, targetPhaseIndex, buildId);
        var approvalEmailAddress = FirstNonBlank(
            Input.ApprovalEmailAddress,
            _options.ApprovalEmailAddress,
            "release.approver@example.invalid");
        var outboxRoot = FirstNonBlank(
            Input.OutboxRoot,
            _options.OutboxRoot,
            Environment.GetEnvironmentVariable("WORKFLOW_BUILDER_DEV_OUTBOX"),
            Path.Combine(Path.GetTempPath(), "workflow-builder-outbox"));
        Directory.CreateDirectory(outboxRoot);

        var messagePath = Path.Combine(
            outboxRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{CiCdPaths.SafeBuildId(buildId)}-approval.eml");
        await File.WriteAllTextAsync(messagePath, CreateEmailBody(buildId, approvalLink, approvalEmailAddress));

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

    private string ResolveTriggerBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(Input.TriggerBaseUrl))
        {
            return Input.TriggerBaseUrl.Trim().TrimEnd('/');
        }

        return (_options.TriggerBaseUrl ?? new Uri("http://localhost:5180")).ToString().TrimEnd('/');
    }

    private static string BuildApprovalLink(
        string baseUrl,
        string phaseSeriesKey,
        int targetPhaseIndex,
        string buildId) =>
        $"{baseUrl}/api/phase-trigger-links/activate?seriesKey={Uri.EscapeDataString(phaseSeriesKey)}&targetPhaseIndex={targetPhaseIndex}&BuildId={Uri.EscapeDataString(buildId)}";

    private string CreateEmailBody(string buildId, string approvalLink, string approvalEmailAddress) =>
        $"""
        To: {approvalEmailAddress}
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

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}
