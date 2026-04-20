using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Backend.Interfaces.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.PluginSeeder;

internal sealed partial class PluginSeedingHostedService : BackgroundService
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly IPluginBlobNameParsingUtility _blobNameParser;
    private readonly PluginSeedingOptions _options;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<PluginSeedingHostedService> _logger;

    public PluginSeedingHostedService(
        BlobContainerClient blobContainerClient,
        IPluginBlobNameParsingUtility blobNameParser,
        IOptions<PluginSeedingOptions> options,
        IHostApplicationLifetime applicationLifetime,
        ILogger<PluginSeedingHostedService> logger)
    {
        _blobContainerClient = blobContainerClient;
        _blobNameParser = blobNameParser;
        _options = options.Value;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Plugin seeding is disabled.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_options.BundlesRoot))
            {
                _logger.LogWarning("Plugin seeding bundles root is not configured.");
                return;
            }

            var bundlesRoot = Path.GetFullPath(_options.BundlesRoot);
            if (!Directory.Exists(bundlesRoot))
            {
                _logger.LogWarning(
                    "Plugin seeding bundles root {BundlesRoot} does not exist; skipping seeding.",
                    bundlesRoot);
                return;
            }

            _logger.LogInformation(
                "Starting plugin bundle seeding from {BundlesRoot}.",
                bundlesRoot);

            var uploadedCount = 0;
            var skippedExistingCount = 0;
            var skippedInvalidCount = 0;
            var failedCount = 0;
            var seenBlobNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var bundlePath in Directory.EnumerateFiles(
                         bundlesRoot,
                         "*.zip",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                stoppingToken.ThrowIfCancellationRequested();

                var blobName = Path.GetFileName(bundlePath);
                if (!seenBlobNames.Add(blobName))
                {
                    _logger.LogInformation(
                        "Skipping duplicate bundle path {BundlePath} because blob {BlobName} was already considered.",
                        bundlePath,
                        blobName);
                    continue;
                }

                if (!CanonicalBundleNamePattern().IsMatch(blobName))
                {
                    skippedInvalidCount++;
                    _logger.LogDebug(
                        "Skipping non-canonical bundle {BundlePath}.",
                        bundlePath);
                    continue;
                }

                try
                {
                    var pluginInfo = _blobNameParser.Parse(blobName);
                    var blobClient = _blobContainerClient.GetBlobClient(pluginInfo.BlobName);

                    if (await blobClient.ExistsAsync(stoppingToken))
                    {
                        skippedExistingCount++;
                        _logger.LogInformation(
                            "Plugin bundle {BlobName} already exists in blob storage; skipping upload.",
                            pluginInfo.BlobName);
                        continue;
                    }

                    await using var stream = File.OpenRead(bundlePath);
                    await blobClient.UploadAsync(stream, overwrite: false, cancellationToken: stoppingToken);
                    uploadedCount++;

                    _logger.LogInformation(
                        "Uploaded plugin bundle {BlobName} from {BundlePath}.",
                        pluginInfo.BlobName,
                        bundlePath);
                }
                catch (FormatException ex)
                {
                    skippedInvalidCount++;
                    _logger.LogWarning(
                        ex,
                        "Skipping bundle {BundlePath} because the blob name is invalid.",
                        bundlePath);
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    skippedExistingCount++;
                    _logger.LogInformation(
                        "Plugin bundle {BlobName} already exists in blob storage; skipping upload.",
                        blobName);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogWarning(
                        ex,
                        "Failed to upload plugin bundle from {BundlePath}.",
                        bundlePath);
                }
            }

            _logger.LogInformation(
                "Plugin seeding finished. Uploaded: {UploadedCount}, existing: {SkippedExistingCount}, invalid: {SkippedInvalidCount}, failed: {FailedCount}.",
                uploadedCount,
                skippedExistingCount,
                skippedInvalidCount,
                failedCount);
        }
        finally
        {
            _applicationLifetime.StopApplication();
        }
    }

    [GeneratedRegex(@"^(?<name>.+)_(?<version>\d+(\.\d+){0,3})\.zip$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalBundleNamePattern();
}
