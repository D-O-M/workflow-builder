using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.PluginLoader.Services;

internal class PluginLoaderService : BackgroundService
{
    private readonly IPluginDiscoveryService _discoveryService;
    private readonly IPluginBundleProcessor _bundleProcessor;
    private readonly ILogger<PluginLoaderService> _logger;
    private readonly TimeSpan _pollInterval;

    public PluginLoaderService(
        IPluginDiscoveryService discoveryService,
        IPluginBundleProcessor bundleProcessor,
        ILogger<PluginLoaderService> logger)
        : this(discoveryService, bundleProcessor, logger, TimeSpan.FromSeconds(5))
    {
    }

    internal PluginLoaderService(
        IPluginDiscoveryService discoveryService,
        IPluginBundleProcessor bundleProcessor,
        ILogger<PluginLoaderService> logger,
        TimeSpan pollInterval)
    {
        _discoveryService = discoveryService;
        _bundleProcessor = bundleProcessor;
        _logger = logger;
        _pollInterval = pollInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting plugin loader service with interval {Interval}.", _pollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during plugin discovery cycle.");
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Plugin loader service is stopping.");
    }

    internal async Task ExecuteCycleAsync(CancellationToken cancellationToken)
    {
        var pendingPlugins = await _discoveryService.GetNewPluginsAsync(cancellationToken);
        foreach (var plugin in pendingPlugins)
        {
            try
            {
                await _bundleProcessor.ProcessAsync(plugin, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process plugin {Plugin} ({Version}) from blob {BlobName}.", plugin.PluginName, plugin.Version, plugin.BlobName);
            }
        }
    }
}
