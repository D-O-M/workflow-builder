namespace Backend.Engine;

public sealed class PluginResultFinalizationWorker : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IStepExecutionService _stepExecutionService;
    private readonly ILogger<PluginResultFinalizationWorker> _logger;
    private readonly PluginResultFinalizationQueue _queue;

    public PluginResultFinalizationWorker(
        IConfiguration configuration,
        IStepExecutionService stepExecutionService,
        ILogger<PluginResultFinalizationWorker> logger,
        PluginResultFinalizationQueue queue)
    {
        _configuration = configuration;
        _stepExecutionService = stepExecutionService;
        _logger = logger;
        _queue = queue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxConcurrentFinalizations = Math.Clamp(
            _configuration.GetValue(
                "PluginRuntime:MaxConcurrentResultFinalizations",
                Environment.ProcessorCount * 4),
            1,
            512);

        _logger.LogInformation(
            "Plugin result finalization worker starting with up to {MaxConcurrentFinalizations} concurrent finalizations.",
            maxConcurrentFinalizations);

        var workers = Enumerable.Range(0, maxConcurrentFinalizations)
            .Select(_ => ProcessResultsAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task ProcessResultsAsync(CancellationToken stoppingToken)
    {
        await foreach (var result in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _stepExecutionService.HandlePluginResultAsync(result, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled plugin result finalization failure for process step {ProcessStepId}.", result.JobId);
            }
        }
    }
}
