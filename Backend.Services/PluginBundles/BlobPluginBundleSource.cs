using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Backend.Interfaces.Utilities;
using Backend.Models;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Backend.Services.PluginBundles;

public sealed class BlobPluginBundleSource : IPluginBundleSource
{
    private static readonly Regex BlobNamePattern = new(
        @"^(?<name>.+)_(?<version>\d+(\.\d+){0,3})\.zip$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly BlobContainerClient _blobContainerClient;
    private readonly IPluginArchiveExtractionUtility _archiveExtractor;
    private readonly ILogger<BlobPluginBundleSource> _logger;

    public BlobPluginBundleSource(
        BlobContainerClient blobContainerClient,
        IPluginArchiveExtractionUtility archiveExtractor,
        ILogger<BlobPluginBundleSource> logger)
    {
        _blobContainerClient = blobContainerClient;
        _archiveExtractor = archiveExtractor;
        _logger = logger;
    }

    public async Task ExtractBundleAsync(
        PluginBlobInfo pluginInfo,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        var blobClient = await ResolveBlobClientAsync(pluginInfo, cancellationToken);
        await _archiveExtractor.ExtractAsync(blobClient, destinationDirectory, cancellationToken);
    }

    private async Task<BlobClient> ResolveBlobClientAsync(
        PluginBlobInfo pluginInfo,
        CancellationToken cancellationToken)
    {
        var exactBlobClient = _blobContainerClient.GetBlobClient(pluginInfo.BlobName);
        if (await exactBlobClient.ExistsAsync(cancellationToken))
        {
            return exactBlobClient;
        }

        var fallbackBlobName = await FindSemanticallyEquivalentBlobNameAsync(
            pluginInfo,
            cancellationToken);

        if (fallbackBlobName is not null)
        {
            _logger.LogInformation(
                "Resolved plugin bundle {RequestedBlobName} to equivalent blob {ResolvedBlobName}.",
                pluginInfo.BlobName,
                fallbackBlobName);
            return _blobContainerClient.GetBlobClient(fallbackBlobName);
        }

        return exactBlobClient;
    }

    private async Task<string?> FindSemanticallyEquivalentBlobNameAsync(
        PluginBlobInfo pluginInfo,
        CancellationToken cancellationToken)
    {
        await foreach (var blobItem in _blobContainerClient.GetBlobsAsync(
                           traits: BlobTraits.None,
                           states: BlobStates.None,
                           prefix: $"{pluginInfo.PluginName}_",
                           cancellationToken: cancellationToken))
        {
            if (!TryParse(blobItem.Name, out var parsedPluginInfo))
            {
                continue;
            }

            if (string.Equals(parsedPluginInfo.PluginName, pluginInfo.PluginName, StringComparison.Ordinal) &&
                parsedPluginInfo.Version == pluginInfo.Version)
            {
                return blobItem.Name;
            }
        }

        return null;
    }

    private static bool TryParse(string blobName, out PluginBlobInfo pluginInfo)
    {
        var match = BlobNamePattern.Match(blobName);
        if (!match.Success)
        {
            pluginInfo = default;
            return false;
        }

        var name = match.Groups["name"].Value;
        var versionString = match.Groups["version"].Value;
        if (!Version.TryParse(versionString, out var version))
        {
            pluginInfo = default;
            return false;
        }

        pluginInfo = new PluginBlobInfo(blobName, name, version);
        return true;
    }
}
