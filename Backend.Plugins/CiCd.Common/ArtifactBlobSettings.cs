using Azure.Storage.Blobs;

namespace CiCd.Common;

public static class ArtifactBlobSettings
{
    public static BlobContainerClient CreateArtifactsContainerClient(string? configuredContainerName = null)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("AzureStorage__ConnectionString")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__BlobConnection")
            ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidDataException("Blob storage connection string is not configured for artifact storage.");
        }

        var containerName = string.IsNullOrWhiteSpace(configuredContainerName)
            ? Environment.GetEnvironmentVariable("AzureStorage__BlobContainers__Artifacts__ContainerName") ?? "artifacts"
            : configuredContainerName.Trim();

        return new BlobContainerClient(connectionString, containerName);
    }
}
