using Backend.Services.ProcessStepQueue;

namespace Backend.Engine;

public sealed class ProcessStepDispatchWorker : BackgroundService
{
    private readonly ProcessStepDispatchService _dispatchService;
    private readonly ProcessStepDispatchOptions _options;
    private readonly ILogger<ProcessStepDispatchWorker> _logger;

    public ProcessStepDispatchWorker(
        ProcessStepDispatchService dispatchService,
        ProcessStepDispatchOptions options,
        ILogger<ProcessStepDispatchWorker> logger)
    {
        _dispatchService = dispatchService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _dispatchService.EnsureDispatchAvailableForExecution();

        _logger.LogInformation("Process-step dispatch worker starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _dispatchService.DrainDueDispatchesAsync(
                    Math.Max(1, _options.BatchSize),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Process-step dispatch worker failed while draining due dispatches.");
            }

            try
            {
                await Task.Delay(EffectivePollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private TimeSpan EffectivePollInterval =>
        _options.PollInterval > TimeSpan.Zero
            ? _options.PollInterval
            : TimeSpan.FromSeconds(1);
}
