using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Engine.Messages;
using Backend.Interfaces.Repositories;

namespace Backend.Engine;

public class QueueConsumer : BackgroundService
{
    private readonly IAgentManager _agentManager;
    private readonly JobManager _jobManager;
    private readonly ServiceBusReceiver _serviceBusReceiver;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger _logger;

    public QueueConsumer(
        IAgentManager agentManager,
        JobManager jobManager,
        ServiceBusReceiver serviceBusReceiver,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<QueueConsumer> logger)
    {
        _agentManager = agentManager;
        _jobManager = jobManager;
        _serviceBusReceiver = serviceBusReceiver;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await _serviceBusReceiver.ReceiveMessageAsync(
                maxWaitTime: null,
                cancellationToken: stoppingToken);

            if (message is null)
            {
                continue;
            }

            try
            {
                var job = JsonSerializer.Deserialize<WorkflowTaskInitialisationMessage>(message.Body);

                if (job is null)
                {
                    await _serviceBusReceiver.AbandonMessageAsync(
                        message,
                        cancellationToken: stoppingToken);
                    continue;
                }

                await using var scope = _serviceScopeFactory.CreateAsyncScope();

                var workflowBlueprintRepository = scope.ServiceProvider
                    .GetRequiredService<IWorkflowBlueprintRepository>();

                var blueprint = await workflowBlueprintRepository
                    .GetFullBlueprintById(job.WorkflowBlueprintId, stoppingToken);

                var task = blueprint.AllTasks
                    .FirstOrDefault(t => t.Id == job.WorkflowTaskId);

                if (task is null || !_agentManager.AgentExists(task.SchemaId))
                {
                    await _serviceBusReceiver.AbandonMessageAsync(
                        message,
                        cancellationToken: stoppingToken);
                    continue;
                }

                var context = ActivatorUtilities.CreateInstance<JobContext>(
                    scope.ServiceProvider,
                    job,
                    message,
                    _serviceBusReceiver,
                    blueprint);

                var started = await _jobManager.StartJob(context, stoppingToken);

                if (!started)
                {
                    await _serviceBusReceiver.AbandonMessageAsync(
                        message,
                        cancellationToken: stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process workflow task message {MessageId}", message.MessageId);
                await _serviceBusReceiver.AbandonMessageAsync(
                    message,
                    cancellationToken: stoppingToken);
            }
        }
    }
}
