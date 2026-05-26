using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Repositories;

public sealed class ProcessStateWarmupHostedService : IHostedService
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly IProcessStepRepository _processStepRepository;
    private readonly IProcessInstanceRepository _processInstanceRepository;
    private readonly ILogger<ProcessStateWarmupHostedService> _logger;

    public ProcessStateWarmupHostedService(
        IProcessStepRepository processStepRepository,
        IProcessInstanceRepository processInstanceRepository,
        ILogger<ProcessStateWarmupHostedService> logger)
    {
        _processStepRepository = processStepRepository;
        _processInstanceRepository = processInstanceRepository;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming up process state repositories.");

        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(StartupTimeout);

        while (!startupTimeout.IsCancellationRequested)
        {
            try
            {
                await ProbeProcessStateAsync(startupTimeout.Token);
                await ProbeProcessStepStateAsync(startupTimeout.Token);

                _logger.LogInformation("Process state repositories are ready.");
                return;
            }
            catch (Exception exception) when (!startupTimeout.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Process state warmup is still waiting for Cosmos readiness.");
            }

            await Task.Delay(RetryDelay, startupTimeout.Token);
        }

        throw new InvalidOperationException(
            $"Process state repositories were not ready within {StartupTimeout}.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ProbeProcessStateAsync(CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(AttemptTimeout);

        try
        {
            await _processInstanceRepository.GetProcessStateById(
                Guid.NewGuid(),
                blueprintVersionId: 0,
                attemptTimeout.Token);
        }
        catch (EntityNotFoundException<ProcessInstanceRecord>)
        {
            // A 404 confirms that the process container is reachable and the request completed.
        }
    }

    private async Task ProbeProcessStepStateAsync(CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(AttemptTimeout);

        var processId = Guid.NewGuid();
        try
        {
            await _processStepRepository.GetProcessStepById(
                Guid.NewGuid(),
                processId,
                attemptTimeout.Token);
        }
        catch (EntityNotFoundException<ProcessStepRecord>)
        {
            // A 404 confirms that the process-step container is reachable and the request completed.
        }
    }
}
