using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Dcc.Common;

public sealed class DccXmlBlobClient : IDccXmlBlobClient
{
    private readonly BlobContainerClient _container;

    public DccXmlBlobClient(BlobContainerClient container)
    {
        _container = container;
    }

    public string ContainerName => _container.Name;

    public static DccXmlBlobClient Create(string? configuredContainerName = null)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("DCC_XML_BLOB_CONNECTION_STRING")
            ?? Environment.GetEnvironmentVariable("ConnectionStrings__BlobConnection")
            ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage")
            ?? throw new InvalidDataException("Blob storage connection string is not configured for DCC XML storage.");

        var containerName = string.IsNullOrWhiteSpace(configuredContainerName)
            ? Environment.GetEnvironmentVariable("DCC_XML_BLOB_CONTAINER")
                ?? Environment.GetEnvironmentVariable("AzureStorage__BlobContainers__Artifacts__ContainerName")
                ?? "artifacts"
            : configuredContainerName.Trim();

        return new DccXmlBlobClient(new BlobContainerClient(connectionString, containerName));
    }

    public async Task UploadTextAsync(string blobName, string content, string contentType = "application/xml")
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            throw new InvalidDataException("A DCC XML blob name is required.");
        }

        await _container.CreateIfNotExistsAsync();
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        await _container.GetBlobClient(blobName).UploadAsync(
            stream,
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType
                }
            },
            CancellationToken.None);
    }

    public async Task<string> DownloadTextAsync(string blobName)
    {
        if (string.IsNullOrWhiteSpace(blobName))
        {
            throw new InvalidDataException("A DCC XML blob name is required.");
        }

        var response = await _container.GetBlobClient(blobName).DownloadContentAsync();
        return response.Value.Content.ToString();
    }
}
