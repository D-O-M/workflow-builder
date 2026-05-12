using System.IO.Compression;
using System.Text.Json;
using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.DeployArtifactSimulation;

public sealed class DeployArtifactSimulationPlugin : IPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public required DeployArtifactInput Input { get; init; }
    public DeployArtifactResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        if (string.IsNullOrWhiteSpace(Input.ArtifactPackageBlobName))
        {
            throw new InvalidDataException("ArtifactPackageBlobName is required.");
        }

        if (string.IsNullOrWhiteSpace(Input.ArtifactManifestBlobName))
        {
            throw new InvalidDataException("ArtifactManifestBlobName is required.");
        }

        var safeBuildId = CiCdPaths.SafeBuildId(buildId);
        var environmentName = string.IsNullOrWhiteSpace(Input.EnvironmentName) ? "Development" : Input.EnvironmentName.Trim();
        var releaseName = string.IsNullOrWhiteSpace(Input.ReleaseName) ? "workflow-builder-demo" : Input.ReleaseName.Trim();
        var deploymentRoot = string.IsNullOrWhiteSpace(Input.DeploymentRoot)
            ? Path.Combine(Path.GetTempPath(), "workflow-builder-deployments")
            : Input.DeploymentRoot.Trim();
        var deploymentPath = Path.Combine(deploymentRoot, environmentName, releaseName, safeBuildId);
        var scratchPath = CiCdPaths.GetScratchPath(buildId, "deploy-artifact");
        var packagePath = Path.Combine(scratchPath, "artifact.zip");
        var manifestPath = Path.Combine(scratchPath, "manifest.json");

        if (Directory.Exists(deploymentPath))
        {
            Directory.Delete(deploymentPath, recursive: true);
        }
        Directory.CreateDirectory(deploymentPath);

        var container = ArtifactBlobSettings.CreateArtifactsContainerClient(Input.ArtifactContainerName);
        await container.GetBlobClient(Input.ArtifactPackageBlobName).DownloadToAsync(packagePath);
        await container.GetBlobClient(Input.ArtifactManifestBlobName).DownloadToAsync(manifestPath);

        ZipFile.ExtractToDirectory(packagePath, deploymentPath, overwriteFiles: true);

        var deployedAt = DateTimeOffset.UtcNow;
        var reportPath = Path.Combine(deploymentPath, "deployment-report.json");
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(new
            {
                BuildId = buildId,
                EnvironmentName = environmentName,
                ReleaseName = releaseName,
                DeploymentPath = deploymentPath,
                Input.ArtifactContainerName,
                Input.ArtifactPackageBlobName,
                Input.ArtifactManifestBlobName,
                Manifest = await File.ReadAllTextAsync(manifestPath),
                DeployedAt = deployedAt
            }, JsonOptions));

        Result = new DeployArtifactResult
        {
            BuildId = buildId,
            EnvironmentName = environmentName,
            DeploymentPath = deploymentPath,
            DeploymentReportPath = reportPath,
            DeployedAt = deployedAt.ToString("O")
        };
    }
}
