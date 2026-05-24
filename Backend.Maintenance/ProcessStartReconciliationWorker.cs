namespace Backend.Maintenance;

public sealed class ProcessStartReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessStartReconciliationOptions _options;
    private readonly ILogger<ProcessStartReconciliationWorker> _logger;

    public ProcessStartReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ProcessStartReconciliationOptions options,
        ILogger<ProcessStartReconciliationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Process-start reconciliation worker is disabled.");
            return;
        }

        _logger.LogInformation("Process-start reconciliation worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ProcessStartReconciliationService>();
                await service.ReconcileOnceAsync(_options, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Process-start reconciliation worker failed while repairing process starts.");
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
