using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;

namespace Backend.Services.ProcessStepQueue;

public sealed class ServiceBusProcessStepQueueReader : IProcessStepQueueReader
{
    private readonly ServiceBusReceiver _receiver;

    public ServiceBusProcessStepQueueReader(ServiceBusReceiver receiver)
    {
        _receiver = receiver;
    }

    public async Task<IProcessStepQueueMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var message = await _receiver.ReceiveMessageAsync(cancellationToken: cancellationToken);
        return message is null ? null : new ServiceBusProcessStepQueueMessage(_receiver, message);
    }
}
