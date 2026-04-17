using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;

namespace Backend.Services.WorkflowTaskQueue;

public sealed class ServiceBusWorkflowTaskQueueReader : IWorkflowTaskQueueReader
{
    private readonly ServiceBusReceiver _receiver;

    public ServiceBusWorkflowTaskQueueReader(ServiceBusReceiver receiver)
    {
        _receiver = receiver;
    }

    public async Task<IWorkflowTaskQueueMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var message = await _receiver.ReceiveMessageAsync(cancellationToken: cancellationToken);
        return message is null ? null : new ServiceBusWorkflowTaskQueueMessage(_receiver, message);
    }
}
