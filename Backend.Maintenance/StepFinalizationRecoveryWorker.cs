namespace Backend.Maintenance;

public sealed class StepFinalizationRecoveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StepFinalizationRecoveryOptions _options;
    private readonly ILogger<StepFinalizationRecoveryWorker> _logger;

    public StepFinalizationRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        StepFinalizationRecoveryOptions options,
        ILogger<StepFinalizationRecoveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Step finalization recovery worker is disabled.");
            return;
        }

        _logger.LogInformation("Step finalization recovery worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<StepFinalizationRecoveryService>();
                await service.RecoverDueFinalizationsAsync(_options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Step finalization recovery worker failed while recovering due finalizations.");
            }

            try
            {
                await Task.Delay(_options.EffectivePollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
