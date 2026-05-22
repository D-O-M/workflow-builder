using Backend.Services.PluginRuntimeStatus;

namespace Backend.Engine;

public sealed class PluginRuntimeStatusHeartbeatWorker : BackgroundService
{
    private readonly PluginTaskVersionRuntimeStatusPublisher _publisher;
    private readonly PluginRuntimeStatusOptions _options;
    private readonly ILogger<PluginRuntimeStatusHeartbeatWorker> _logger;

    public PluginRuntimeStatusHeartbeatWorker(
        PluginTaskVersionRuntimeStatusPublisher publisher,
        PluginRuntimeStatusOptions options,
        ILogger<PluginRuntimeStatusHeartbeatWorker> logger)
    {
        _publisher = publisher;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, stoppingToken);
                await _publisher.RefreshKnownObservationsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin runtime status heartbeat failed.");
            }
        }
    }
}
