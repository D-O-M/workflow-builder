using Azure.Storage.Blobs;
using Backend.Interfaces.Utilities;
using Backend.Models;

namespace Backend.Services.PluginBundles;

public sealed class BlobPluginBundleSource : IPluginBundleSource
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly IPluginArchiveExtractionUtility _archiveExtractor;

    public BlobPluginBundleSource(
        BlobContainerClient blobContainerClient,
        IPluginArchiveExtractionUtility archiveExtractor)
    {
        _blobContainerClient = blobContainerClient;
        _archiveExtractor = archiveExtractor;
    }

    public Task ExtractBundleAsync(
        PluginBlobInfo pluginInfo,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var blobClient = _blobContainerClient.GetBlobClient(pluginInfo.BlobName);
        return _archiveExtractor.ExtractAsync(blobClient, destinationDirectory, cancellationToken);
    }
}
