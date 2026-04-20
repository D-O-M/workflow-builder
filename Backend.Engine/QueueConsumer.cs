using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;

namespace Backend.Engine;

public class QueueConsumer : BackgroundService
{
    private readonly IAgentManager _agentManager;
    private readonly JobManager _jobManager;
    private readonly NanoServiceOrchestrator _nanoServiceOrchestrator;
    private readonly IWorkflowTaskQueueReader _workflowTaskQueueReader;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    public QueueConsumer(
        IAgentManager agentManager,
        JobManager jobManager,
        NanoServiceOrchestrator nanoServiceOrchestrator,
        IWorkflowTaskQueueReader workflowTaskQueueReader,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<QueueConsumer> logger)
    {
        _agentManager = agentManager;
        _jobManager = jobManager;
        _nanoServiceOrchestrator = nanoServiceOrchestrator;
        _workflowTaskQueueReader = workflowTaskQueueReader;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await _workflowTaskQueueReader.ReceiveAsync(stoppingToken);

            if (message is null)
            {
                continue;
            }

            try
            {
                var job = message.Payload;

                await using var scope = _serviceScopeFactory.CreateAsyncScope();

                var workflowBlueprintRepository = scope.ServiceProvider
                    .GetRequiredService<IWorkflowBlueprintRepository>();

                var blueprint = await workflowBlueprintRepository
                    .GetFullBlueprintById(job.WorkflowBlueprintId, stoppingToken);

                var task = blueprint.AllTasks
                    .FirstOrDefault(t => t.Id == job.BlueprintTaskId);

                if (task is null)
                {
                    await message.AbandonAsync(stoppingToken);
                    continue;
                }

                if (!_nanoServiceOrchestrator.TryResolveTaskVersion(
                        task.TaskDefinitionId,
                        task.AllowedMajor,
                        task.AllowedMinor,
                        out var runtimeVersion))
                {
                    await _nanoServiceOrchestrator.ReloadAsync(stoppingToken);

                    if (!_nanoServiceOrchestrator.TryResolveTaskVersion(
                            task.TaskDefinitionId,
                            task.AllowedMajor,
                            task.AllowedMinor,
                            out runtimeVersion))
                    {
                        await message.AbandonAsync(stoppingToken);
                        continue;
                    }
                }

                task.ResolvedTaskVersionId = runtimeVersion.Id;
                task.ResolvedVersion = runtimeVersion.Version;

                if (!_agentManager.AgentExists(task.ResolvedTaskVersionId))
                {
                    await message.AbandonAsync(stoppingToken);
                    continue;
                }

                var context = ActivatorUtilities.CreateInstance<JobContext>(
                    scope.ServiceProvider,
                    job,
                    message,
                    blueprint);

                var started = await _jobManager.StartJob(context, stoppingToken);

                if (!started)
                {
                    await message.AbandonAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process workflow task message {MessageId}", message.MessageId);
                await message.AbandonAsync(stoppingToken);
            }
        }
    }
}
