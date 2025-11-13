using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Backend.DTOs;
using Backend.Interfaces.Utilities;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
namespace Backend.Services;

internal sealed class PluginBundleProcessingService : IPluginBundleProcessingService
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly IPluginWorkspaceFactoryUtility _workspaceFactory;
    private readonly IPluginArchiveExtractionUtility _archiveExtractor;
    private readonly IPluginManifestParsingUtility _manifestParser;
    private readonly IPluginSchemaAggregationUtility _schemaAggregator;
    private readonly ILogger<PluginBundleProcessingService> _logger;

    public PluginBundleProcessingService(
        BlobContainerClient blobContainerClient,
        IPluginWorkspaceFactoryUtility workspaceFactory,
        IPluginArchiveExtractionUtility archiveExtractor,
        IPluginManifestParsingUtility manifestReader,
        IPluginSchemaAggregationUtility schemaAggregator,
        ILogger<PluginBundleProcessingService> logger)
    {
        _blobContainerClient = blobContainerClient;
        _workspaceFactory = workspaceFactory;
        _archiveExtractor = archiveExtractor;
        _manifestParser = manifestReader;
        _schemaAggregator = schemaAggregator;
        _logger = logger;
    }

    public async Task ProcessAsync(PluginBlobInfo pluginInfo, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing plugin {Plugin} ({Version}) from blob {BlobName}", pluginInfo.PluginName, pluginInfo.Version, pluginInfo.BlobName);

        var blobClient = _blobContainerClient.GetBlobClient(pluginInfo.BlobName);
        var workspaceDirectory = await _workspaceFactory.CreateWorkspaceAsync(pluginInfo.PluginName, pluginInfo.Version, cancellationToken);

        await _archiveExtractor.ExtractAsync(blobClient, workspaceDirectory, cancellationToken);

        var manifest = await _manifestParser.ReadAsync(workspaceDirectory, cancellationToken);
        if (manifest is null)
        {
            _logger.LogWarning("Manifest not found for plugin {Plugin} ({Version}). Processing aborted.", pluginInfo.PluginName, pluginInfo.Version);
            return;
        }

        var schemas = await _schemaAggregator.LoadAsync(workspaceDirectory, cancellationToken);
        LogProcessingSummary(pluginInfo, manifest, schemas);
        LogEntryPoint(workspaceDirectory);
    }

    private void LogProcessingSummary(PluginBlobInfo pluginInfo, ManifestFile manifest, PluginSchemaResult? schemas)
    {
        _logger.LogInformation(
            "Manifest parsed for plugin {Plugin} ({Version}). Inputs: {InputCount}, Outputs: {OutputCount}.",
            pluginInfo.PluginName,
            pluginInfo.Version,
            manifest.InputChannels?.Count ?? 0,
            manifest.OutputChannels?.Count ?? 0);

        if (schemas is null)
        {
            _logger.LogInformation("No schema information available for plugin {Plugin} ({Version}).", pluginInfo.PluginName, pluginInfo.Version);
            return;
        }

        _logger.LogInformation(
            "Loaded {SchemaCount} schema definition(s) for plugin {Plugin} ({Version}).",
            schemas.DefinitionCount,
            pluginInfo.PluginName,
            pluginInfo.Version);
    }

    private void LogEntryPoint(string workspaceDirectory)
    {
        var appDir = Path.Combine(workspaceDirectory, "app");
        if (!Directory.Exists(appDir))
        {
            _logger.LogWarning("No app directory found at {AppDirectory}", appDir);
            return;
        }

        var exePath = Directory.EnumerateFiles(appDir, "*.exe").FirstOrDefault();
        var dllPath = Directory.EnumerateFiles(appDir, "*.dll").FirstOrDefault();

        if (exePath is not null)
        {
            _logger.LogInformation("App entry (exe): {ExePath}", exePath);
        }
        else if (dllPath is not null)
        {
            _logger.LogInformation("App entry (dll): {DllPath}", dllPath);
        }
        else
        {
            _logger.LogWarning("No app entry point found in {AppDirectory}", appDir);
        }
    }
}
