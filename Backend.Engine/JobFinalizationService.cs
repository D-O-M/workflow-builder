using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Engine.Messages;

namespace Backend.Engine;

public class JobFinalizationService
{
    private readonly ILogger<JobFinalizationService> _logger;
    private readonly ServiceBusSender _serviceBusSender;

    public JobFinalizationService(
        ILogger<JobFinalizationService> logger,
        ServiceBusSender serviceBusSender)
    {
        _logger = logger;
        _serviceBusSender = serviceBusSender;
    }

    public async Task FinalizeSuccess(
        JobContext context,
        CancellationToken cancellationToken)
    {
        await context.CompleteMessageAsync(cancellationToken);
        await EnqueueNextTasksAsync(context, cancellationToken);
    }

    public async Task FinalizeFailureAsync(JobContext context, CancellationToken cancellationToken)
    {
        await context.AbandonMessageAsync(cancellationToken);
    }

    private async Task EnqueueNextTasksAsync(JobContext context, CancellationToken cancellationToken)
    {
        foreach (var task in context.NextTasks)
        {
            if (!context.OutputChannelsPopulated.Contains(task.InputChannel.Name))
            {
                continue;
            }

            var job = new WorkflowTaskInitialisationMessage
            {
                WorkflowInstanceId = context.WorkflowInstanceId,
                WorkflowBlueprintId = context.WorkflowBlueprintId,
                WorkflowTaskInstanceId = Guid.NewGuid(),
                WorkflowTaskId = task.Id
            };

            var message = new ServiceBusMessage(BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(job)))
            {
                ContentType = "application/json"
            };

            await _serviceBusSender.SendMessageAsync(message, cancellationToken);
            _logger.LogInformation(
                "Enqueued downstream task {TaskId} for workflow instance {WorkflowInstanceId}",
                task.Id,
                context.WorkflowInstanceId);
        }
    }
}
