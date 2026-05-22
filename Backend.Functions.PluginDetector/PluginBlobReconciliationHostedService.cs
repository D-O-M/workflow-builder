using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Services;
using Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.PluginDetector;

internal sealed class PluginBlobReconciliationHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PluginBlobReconciliationHostedService> _logger;

    public PluginBlobReconciliationHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<PluginBlobReconciliationHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Yield();

            using var scope = _scopeFactory.CreateScope();
            var discoveryService = scope.ServiceProvider.GetRequiredService<IPluginDiscoveryService>();
            var bundleProcessingService = scope.ServiceProvider.GetRequiredService<IPluginBundleProcessingService>();
            var ingestionStatusStore = scope.ServiceProvider.GetRequiredService<IPluginIngestionStatusStore>();

            var pendingPlugins = await discoveryService.GetNewPluginsAsync(stoppingToken);
            if (pendingPlugins.Count == 0)
            {
                _logger.LogInformation("Plugin blob reconciliation found no unregistered canonical bundles.");
                return;
            }

            _logger.LogInformation(
                "Plugin blob reconciliation found {PendingCount} unregistered canonical bundle(s).",
                pendingPlugins.Count);

            var processedCount = 0;
            var failedCount = 0;

            foreach (var pluginInfo in pendingPlugins
                         .OrderBy(plugin => plugin.PluginName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(plugin => plugin.Version))
            {
                stoppingToken.ThrowIfCancellationRequested();

                try
                {
                    if (await ingestionStatusStore.IsQuarantinedAsync(pluginInfo.BlobName, stoppingToken))
                    {
                        _logger.LogWarning(
                            "Plugin blob reconciliation skipped quarantined bundle {BlobName}.",
                            pluginInfo.BlobName);
                        continue;
                    }

                    await RecordStatusAsync(
                        ingestionStatusStore,
                        PluginIngestionStatusUpdate.FromPlugin(
                            pluginInfo.BlobName,
                            pluginInfo.PluginName,
                            pluginInfo.Version,
                            PluginIngestionStatus.Detected,
                            "Plugin bundle found during reconciliation."),
                        stoppingToken);
                    await bundleProcessingService.ProcessAsync(pluginInfo, stoppingToken);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    _logger.LogWarning(
                        ex,
                        "Failed to reconcile plugin bundle {BlobName}.",
                        pluginInfo.BlobName);
                }
            }

            _logger.LogInformation(
                "Plugin blob reconciliation completed. Processed: {ProcessedCount}, failed: {FailedCount}.",
                processedCount,
                failedCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Plugin blob reconciliation was cancelled during shutdown.");
        }
    }

    private async Task RecordStatusAsync(
        IPluginIngestionStatusStore ingestionStatusStore,
        PluginIngestionStatusUpdate update,
        CancellationToken cancellationToken)
    {
        try
        {
            await ingestionStatusStore.AppendEventAsync(update, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Unable to record plugin reconciliation status {Status} for {BlobName}.", update.Status, update.BlobName);
        }
    }
}
