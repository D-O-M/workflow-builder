using Backend.Interfaces.Queues;

namespace Backend.Engine;

public sealed class PluginReloadConsumer : BackgroundService
{
    private readonly IPluginReloadQueueReader _pluginReloadQueueReader;
    private readonly NanoServiceOrchestrator _nanoServiceOrchestrator;
    private readonly ILogger<PluginReloadConsumer> _logger;

    public PluginReloadConsumer(
        IPluginReloadQueueReader pluginReloadQueueReader,
        NanoServiceOrchestrator nanoServiceOrchestrator,
        ILogger<PluginReloadConsumer> logger)
    {
        _pluginReloadQueueReader = pluginReloadQueueReader;
        _nanoServiceOrchestrator = nanoServiceOrchestrator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await _pluginReloadQueueReader.ReceiveAsync(stoppingToken);
            if (message is null)
            {
                continue;
            }

            try
            {
                _logger.LogInformation(
                    "Processing plugin reload request for {TaskName} {Version} ({ChangeKind}).",
                    message.Payload.TaskName,
                    message.Payload.Version,
                    message.Payload.ChangeKind);

                await _nanoServiceOrchestrator.ReloadAsync(stoppingToken);
                await message.CompleteAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to process plugin reload request for task version {TaskVersionId}.",
                    message.Payload.TaskVersionId);
                await message.AbandonAsync(stoppingToken);
            }
        }
    }
}
