namespace CiCd.DeployArtifactSimulation;

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DeployArtifactInput
{
    public string BuildId { get; set; } = "";
    public string ArtifactContainerName { get; set; } = "";
    public string ArtifactPackageBlobName { get; set; } = "";
    public string ArtifactManifestBlobName { get; set; } = "";
    public string EnvironmentName { get; set; } = "Development";
    public string DeploymentRoot { get; set; } = "";
    public string ReleaseName { get; set; } = "workflow-builder-demo";
}

[Backend.Plugins.Harness.PluginJsonSerializable]
public sealed class DeployArtifactResult
{
    public string BuildId { get; set; } = "";
    public string EnvironmentName { get; set; } = "";
    public string DeploymentPath { get; set; } = "";
    public string DeploymentReportPath { get; set; } = "";
    public string DeployedAt { get; set; } = "";
}
