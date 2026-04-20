using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging;

namespace Backend.Services.WorkflowTaskQueue;

public sealed class ServiceBusWorkflowTaskQueueWriter : IWorkflowTaskQueueWriter
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusWorkflowTaskQueueWriter> _logger;

    public ServiceBusWorkflowTaskQueueWriter(
        ServiceBusSender sender,
        ILogger<ServiceBusWorkflowTaskQueueWriter> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public Task EnqueueAsync(
        WorkflowTaskInitialisationMessage message,
        CancellationToken cancellationToken)
    {
        var queueMessage = new ServiceBusMessage(
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(message)))
        {
            ContentType = "application/json"
        };

        _logger.LogInformation(
            "Sending workflow initialisation message for workflow {WorkflowInstanceId}, task {WorkflowTaskInstanceId} to queue {QueueName}.",
            message.WorkflowInstanceId,
            message.WorkflowTaskInstanceId,
            _sender.EntityPath);

        return _sender.SendMessageAsync(queueMessage, cancellationToken);
    }
}
