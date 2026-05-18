namespace Backend.Engine;

public sealed class PluginResultFinalizationWorker : IHostedService
{
    private readonly IStepExecutionService _stepExecutionService;
    private readonly PluginResultFinalizationTelemetry _telemetry;
    private readonly ILogger<PluginResultFinalizationWorker> _logger;
    private readonly PluginResultFinalizationQueue _queue;
    private readonly PluginRuntimeOptions _options;
    private readonly CancellationTokenSource _stopImmediately = new();
    private Task? _executingTask;

    public PluginResultFinalizationWorker(
        IStepExecutionService stepExecutionService,
        PluginResultFinalizationTelemetry telemetry,
        ILogger<PluginResultFinalizationWorker> logger,
        PluginResultFinalizationQueue queue,
        PluginRuntimeOptions options)
    {
        _stepExecutionService = stepExecutionService;
        _telemetry = telemetry;
        _logger = logger;
        _queue = queue;
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Plugin result finalization worker starting with up to {MaxConcurrentFinalizations} concurrent finalizations and queue capacity {QueueCapacity}.",
            _options.EffectiveMaxConcurrentResultFinalizations,
            _options.EffectiveResultFinalizationQueueCapacity);

        _executingTask = ExecuteAsync(_stopImmediately.Token);
        return _executingTask.IsCompleted
            ? _executingTask
            : Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();

        if (_executingTask is null)
        {
            return;
        }

        try
        {
            await _executingTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Plugin result finalization worker shutdown timed out before the queue drained; remaining results will rely on process-step redelivery.");
            await _stopImmediately.CancelAsync();
        }
    }

    private async Task ExecuteAsync(CancellationToken stopImmediatelyToken)
    {
        var workers = Enumerable.Range(0, _options.EffectiveMaxConcurrentResultFinalizations)
            .Select(_ => ProcessResultsAsync(stopImmediatelyToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task ProcessResultsAsync(CancellationToken stopImmediatelyToken)
    {
        await foreach (var item in _queue.ReadAllAsync(stopImmediatelyToken))
        {
            try
            {
                await _stepExecutionService.HandlePluginResultAsync(item.Result, stopImmediatelyToken);
                _telemetry.RecordFinalizationCompleted(succeeded: true);
            }
            catch (OperationCanceledException) when (stopImmediatelyToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _telemetry.RecordFinalizationCompleted(succeeded: false);
                _telemetry.RecordFailure(ex);
                _logger.LogError(
                    ex,
                    "Unhandled plugin result finalization failure for process step {ProcessStepId}.",
                    item.Result.JobId);
            }
        }
    }
}
