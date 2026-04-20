using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;
using Microsoft.Extensions.Logging;

namespace Backend.Services.ProcessStepQueue;

public sealed class ServiceBusProcessStepQueueWriter : IProcessStepQueueWriter
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusProcessStepQueueWriter> _logger;

    public ServiceBusProcessStepQueueWriter(
        ServiceBusSender sender,
        ILogger<ServiceBusProcessStepQueueWriter> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public Task EnqueueAsync(
        ProcessStepInitialisationMessage message,
        CancellationToken cancellationToken)
    {
        var queueMessage = new ServiceBusMessage(
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(message)))
        {
            ContentType = "application/json"
        };

        _logger.LogInformation(
            "Sending process step initialisation message for process {ProcessId}, step {ProcessStepId} to queue {QueueName}.",
            message.ProcessId,
            message.ProcessStepId,
            _sender.EntityPath);

        return _sender.SendMessageAsync(queueMessage, cancellationToken);
    }
}
