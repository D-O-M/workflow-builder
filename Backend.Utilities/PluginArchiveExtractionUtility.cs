using System.IO.Compression;
using Azure.Storage.Blobs;
using Backend.Interfaces.Utilities;
using Microsoft.Extensions.Logging;

namespace Backend.Utilities;

public sealed class PluginArchiveExtractionUtility : IPluginArchiveExtractionUtility
{
    private readonly ILogger<PluginArchiveExtractionUtility> _logger;

    public PluginArchiveExtractionUtility(ILogger<PluginArchiveExtractionUtility> logger)
    {
        _logger = logger;
    }

    public async Task ExtractAsync(BlobClient blobClient, string destinationDirectory, CancellationToken cancellationToken)
    {
        var tempArchivePath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}-{Path.GetFileName(blobClient.Name)}");

        try
        {
            await blobClient.DownloadToAsync(tempArchivePath, cancellationToken: cancellationToken);

            using var archive = ZipFile.OpenRead(tempArchivePath);
            ExtractArchiveToDirectory(archive, destinationDirectory, overwrite: true);
            _logger.LogInformation("Extracted blob {BlobName} to {Destination}", blobClient.Name, destinationDirectory);
        }
        finally
        {
            if (File.Exists(tempArchivePath))
            {
                File.Delete(tempArchivePath);
            }
        }
    }

    private static void ExtractArchiveToDirectory(ZipArchive archive, string destinationDirectoryFullPath, bool overwrite)
    {
        destinationDirectoryFullPath = Path.GetFullPath(destinationDirectoryFullPath);
        foreach (var entry in archive.Entries)
        {
            var fullPath = Path.Combine(destinationDirectoryFullPath, entry.FullName);
            var normalized = Path.GetFullPath(fullPath);
            if (!normalized.StartsWith(destinationDirectoryFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(normalized);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(normalized)!);
            if (!overwrite && File.Exists(normalized))
            {
                continue;
            }

            using var entryStream = entry.Open();
            using var outStream = File.Create(normalized);
            entryStream.CopyTo(outStream);
        }
    }
}
