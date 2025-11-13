using Azure.Storage.Blobs;

namespace Backend.Interfaces.Utilities;

public interface IPluginArchiveExtractionUtility
{
    Task ExtractAsync(BlobClient blobClient,
        string destinationDirectory,
        CancellationToken cancellationToken);
}
