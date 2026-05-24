namespace CiCd.PublishArtifacts;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class PublishArtifactsInput
{
    public string BuildId { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string ArtifactName { get; set; } = "workflow-builder-cicd";
    public string ArtifactPathList { get; set; } = "";
    public string ArtifactContainerName { get; set; } = "";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class PublishArtifactsResult
{
    public string BuildId { get; set; } = "";
    public string ArtifactName { get; set; } = "";
    public string ArtifactContainerName { get; set; } = "";
    public string ArtifactPackageBlobName { get; set; } = "";
    public string ArtifactManifestBlobName { get; set; } = "";
    public string ArtifactPackageUri { get; set; } = "";
    public string ArtifactManifestUri { get; set; } = "";
    public string PublishReportPath { get; set; } = "";
}
