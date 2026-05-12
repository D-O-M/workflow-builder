using System.IO.Compression;
using System.Text.Json;
using Backend.Plugins.Harness;
using CiCd.Common;

namespace CiCd.PublishArtifacts;

public sealed class PublishArtifactsPlugin : IPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public required PublishArtifactsInput Input { get; init; }
    public PublishArtifactsResult Result { get; set; } = default!;

    public async Task Execute()
    {
        var buildId = CiCdPaths.ResolveBuildId(Input.BuildId);
        var safeBuildId = CiCdPaths.SafeBuildId(buildId);
        var workspacePath = CiCdPaths.ResolveWorkspacePath(Input.WorkspacePath, null);
        var artifactName = string.IsNullOrWhiteSpace(Input.ArtifactName) ? "workflow-builder-cicd" : Input.ArtifactName.Trim();
        var scratchPath = CiCdPaths.GetScratchPath(buildId, "publish-artifacts");
        var packagePath = Path.Combine(scratchPath, $"{artifactName}-{safeBuildId}.zip");
        var manifestPath = Path.Combine(scratchPath, "manifest.json");
        var reportPath = CiCdPaths.GetLogPath(workspacePath, buildId, "publish-artifacts");
        var paths = ResolveArtifactPaths(workspacePath, buildId);

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        var includedEntries = CreatePackage(packagePath, workspacePath, paths);
        var manifest = new ArtifactManifest
        {
            BuildId = buildId,
            ArtifactName = artifactName,
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspacePath = workspacePath,
            PackageFileName = Path.GetFileName(packagePath),
            IncludedEntries = includedEntries
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

        var container = ArtifactBlobSettings.CreateArtifactsContainerClient(Input.ArtifactContainerName);
        await container.CreateIfNotExistsAsync();
        var packageBlobName = $"cicd/{safeBuildId}/{Path.GetFileName(packagePath)}";
        var manifestBlobName = $"cicd/{safeBuildId}/manifest.json";
        await container.GetBlobClient(packageBlobName).UploadAsync(packagePath, overwrite: true);
        await container.GetBlobClient(manifestBlobName).UploadAsync(manifestPath, overwrite: true);

        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(new
            {
                manifest.BuildId,
                manifest.ArtifactName,
                manifest.CreatedAt,
                Container = container.Name,
                PackageBlobName = packageBlobName,
                ManifestBlobName = manifestBlobName,
                IncludedEntryCount = includedEntries.Count
            }, JsonOptions));

        Result = new PublishArtifactsResult
        {
            BuildId = buildId,
            ArtifactName = artifactName,
            ArtifactContainerName = container.Name,
            ArtifactPackageBlobName = packageBlobName,
            ArtifactManifestBlobName = manifestBlobName,
            ArtifactPackageUri = container.GetBlobClient(packageBlobName).Uri.ToString(),
            ArtifactManifestUri = container.GetBlobClient(manifestBlobName).Uri.ToString(),
            PublishReportPath = reportPath
        };
    }

    private string[] ResolveArtifactPaths(string workspacePath, string buildId)
    {
        var explicitPaths = CiCdPaths.SplitPathList(Input.ArtifactPathList);
        if (explicitPaths.Length > 0)
        {
            return explicitPaths
                .Select(path => CiCdPaths.ResolveWorkspacePath(workspacePath, path))
                .ToArray();
        }

        var safeBuildId = CiCdPaths.SafeBuildId(buildId);
        return
        [
            Path.Combine(workspacePath, ".artifacts", "cicd", safeBuildId),
            Path.Combine(Directory.GetParent(workspacePath)?.FullName ?? workspacePath, ".artifacts", "cicd", safeBuildId),
            Path.Combine(workspacePath, "UI", "dist"),
            Path.Combine(workspacePath, "Backend.Plugins", "_bundles")
        ];
    }

    private static List<string> CreatePackage(
        string packagePath,
        string workspacePath,
        IReadOnlyList<string> paths)
    {
        var includedEntries = new List<string>();
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                var entryName = ToZipEntryName(workspacePath, path);
                archive.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
                includedEntries.Add(entryName);
                continue;
            }

            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var entryName = ToZipEntryName(workspacePath, filePath);
                archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
                includedEntries.Add(entryName);
            }
        }

        if (includedEntries.Count == 0)
        {
            var emptyEntry = archive.CreateEntry("README.txt");
            using var stream = emptyEntry.Open();
            using var writer = new StreamWriter(stream);
            writer.WriteLine("No artifact paths existed when the package was created.");
            includedEntries.Add("README.txt");
        }

        return includedEntries;
    }

    private static string ToZipEntryName(string workspacePath, string filePath)
    {
        var relativePath = Path.GetRelativePath(workspacePath, filePath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            relativePath = Path.Combine("_external", Path.GetFileName(filePath));
        }

        return relativePath.Replace('\\', '/');
    }

    private sealed class ArtifactManifest
    {
        public required string BuildId { get; init; }
        public required string ArtifactName { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required string WorkspacePath { get; init; }
        public required string PackageFileName { get; init; }
        public required List<string> IncludedEntries { get; init; }
    }
}
