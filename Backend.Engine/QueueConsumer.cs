using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Engine.Messages;
using Backend.Interfaces.Repositories;

namespace Backend.Engine;

public class QueueConsumer : BackgroundService
{
    private readonly IAgentManager _agentManager;
    private readonly ServiceBusReceiver _serviceBusReceiver;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWorkflowBlueprintRepository _workflowBlueprintRepository;

    public QueueConsumer(
        IAgentManager agentManager,
        ServiceBusReceiver serviceBusReceiver,
        IWorkflowBlueprintRepository workflowBlueprintRepository)
    {
        _agentManager = agentManager;
        _serviceBusReceiver = serviceBusReceiver;
        _workflowBlueprintRepository = workflowBlueprintRepository;
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

            var job = JsonSerializer.Deserialize<WorkflowTaskExecutionJob>(message.Body);
            if (job is not null)
            {
                // Process the job
                Console.WriteLine($"Processing job: {job.WorkflowTaskInstanceId}");

                var blueprint = await _workflowBlueprintRepository
                    .GetFullBlueprintById(job.WorkflowBlueprintId, stoppingToken);

                var task = blueprint.AllTasks
                    .FirstOrDefault(t => t.Id == job.WorkflowTaskId);

                if (task is null)
                    throw new Exception();

                if (!_agentManager.AgentExists(task.SchemaId))
                {
                    await _serviceBusReceiver
                        .AbandonMessageAsync(message, cancellationToken: stoppingToken);
                    continue;
                }

                var context = ActivatorUtilities.CreateInstance<TaskContext>(
                    _serviceProvider,
                    job,
                    message,
                    blueprint);
            }
        }
    }
}
