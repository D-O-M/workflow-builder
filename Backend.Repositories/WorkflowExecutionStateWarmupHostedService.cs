using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Repositories;

public sealed class WorkflowExecutionStateWarmupHostedService : IHostedService
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly ITaskInstanceRepository _taskInstanceRepository;
    private readonly IWorkflowInstanceRepository<Dictionary<string, JsonElement>> _workflowInstanceRepository;
    private readonly ILogger<WorkflowExecutionStateWarmupHostedService> _logger;

    public WorkflowExecutionStateWarmupHostedService(
        ITaskInstanceRepository taskInstanceRepository,
        IWorkflowInstanceRepository<Dictionary<string, JsonElement>> workflowInstanceRepository,
        ILogger<WorkflowExecutionStateWarmupHostedService> logger)
    {
        _taskInstanceRepository = taskInstanceRepository;
        _workflowInstanceRepository = workflowInstanceRepository;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Warming up workflow execution state repositories.");

        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(StartupTimeout);

        while (!startupTimeout.IsCancellationRequested)
        {
            try
            {
                await ProbeWorkflowStateAsync(startupTimeout.Token);
                await ProbeTaskStateAsync(startupTimeout.Token);

                _logger.LogInformation("Workflow execution state repositories are ready.");
                return;
            }
            catch (Exception exception) when (!startupTimeout.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Workflow execution state warmup is still waiting for Cosmos readiness.");
            }

            await Task.Delay(RetryDelay, startupTimeout.Token);
        }

        throw new InvalidOperationException(
            $"Workflow execution state repositories were not ready within {StartupTimeout}.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ProbeWorkflowStateAsync(CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(AttemptTimeout);

        try
        {
            await _workflowInstanceRepository.GetWorkflowStateById(
                Guid.NewGuid(),
                workflowBlueprintId: 0,
                attemptTimeout.Token);
        }
        catch (EntityNotFoundException<WorkflowObject<Dictionary<string, JsonElement>>>)
        {
            // A 404 confirms that the workflow container is reachable and the request completed.
        }
    }

    private async Task ProbeTaskStateAsync(CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(AttemptTimeout);

        var workflowInstanceId = Guid.NewGuid();
        try
        {
            await _taskInstanceRepository.GetTaskInstanceById(
                Guid.NewGuid(),
                workflowInstanceId,
                attemptTimeout.Token);
        }
        catch (EntityNotFoundException<TaskObject>)
        {
            // A 404 confirms that the tasks container is reachable and the request completed.
        }
    }
}
