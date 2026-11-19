using Backend.Interfaces.Queues;

namespace Backend.Engine;

public sealed class QueueConsumer : BackgroundService
{
    private readonly IProcessStepQueueProcessor _processStepQueueProcessor;
    private readonly StepMessageHandler _stepMessageHandler;
    private readonly ILogger<QueueConsumer> _logger;
    private readonly PluginRuntimeOrchestrator _orchestrator;

    public QueueConsumer(
        IProcessStepQueueProcessor processStepQueueProcessor,
        StepMessageHandler stepMessageHandler,
        ILogger<QueueConsumer> logger, PluginRuntimeOrchestrator orchestrator)
    {
        _processStepQueueProcessor = processStepQueueProcessor;
        _stepMessageHandler = stepMessageHandler;
        _logger = logger;
        _orchestrator = orchestrator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _orchestrator.InitialCatalogReady.WaitAsync(stoppingToken);
        _logger.LogInformation(
            "Process-step queue consumer starting with Service Bus processor concurrency.");

        await _processStepQueueProcessor.StartProcessingAsync(
            _stepMessageHandler.HandleProcessStepMessageAsync,
            stoppingToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        finally
        {
            await _processStepQueueProcessor.StopProcessingAsync(CancellationToken.None);
        }
    }
}
