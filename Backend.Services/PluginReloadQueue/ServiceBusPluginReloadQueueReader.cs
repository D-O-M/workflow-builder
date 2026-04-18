using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;

namespace Backend.Services.PluginReloadQueue;

public sealed class ServiceBusPluginReloadQueueReader : IPluginReloadQueueReader
{
    private readonly ServiceBusReceiver _receiver;

    public ServiceBusPluginReloadQueueReader(ServiceBusReceiver receiver)
    {
        _receiver = receiver;
    }

    public async Task<IPluginReloadQueueMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var message = await _receiver.ReceiveMessageAsync(cancellationToken: cancellationToken);
        return message is null ? null : new ServiceBusPluginReloadQueueMessage(_receiver, message);
    }
}
