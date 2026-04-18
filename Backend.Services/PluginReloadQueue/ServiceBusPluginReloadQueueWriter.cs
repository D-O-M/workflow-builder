using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace Backend.Services.PluginReloadQueue;

public sealed class ServiceBusPluginReloadQueueWriter : IPluginReloadQueueWriter
{
    private readonly ServiceBusSender _sender;

    public ServiceBusPluginReloadQueueWriter(ServiceBusSender sender)
    {
        _sender = sender;
    }

    public Task EnqueueAsync(PluginReloadRequestMessage message, CancellationToken cancellationToken)
    {
        var queueMessage = new ServiceBusMessage(
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(message)))
        {
            ContentType = "application/json"
        };

        return _sender.SendMessageAsync(queueMessage, cancellationToken);
    }
}
