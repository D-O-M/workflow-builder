using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace Backend.Services.WorkflowTaskQueue;

public sealed class ServiceBusWorkflowTaskQueueWriter : IWorkflowTaskQueueWriter
{
    private readonly ServiceBusSender _sender;

    public ServiceBusWorkflowTaskQueueWriter(ServiceBusSender sender)
    {
        _sender = sender;
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

        return _sender.SendMessageAsync(queueMessage, cancellationToken);
    }
}
