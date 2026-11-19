using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
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
            ContentType = "application/json",
            MessageId = ProcessStepDispatchSchedule.CreateMessageId(message.ProcessStepId, message.DispatchGeneration)
        };

        _logger.LogInformation(
            "Sending process step initialisation message for process {ProcessId}, step {ProcessStepId} to queue {QueueName}.",
            message.ProcessId,
            message.ProcessStepId,
            _sender.EntityPath);

        return _sender.SendMessageAsync(queueMessage, cancellationToken);
    }

    public async Task EnqueueBatchAsync(
        IReadOnlyCollection<ProcessStepInitialisationMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var batch = await _sender.CreateMessageBatchAsync(cancellationToken);
        foreach (var message in messages)
        {
            var queueMessage = new ServiceBusMessage(
                BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(message)))
            {
                ContentType = "application/json",
                MessageId = ProcessStepDispatchSchedule.CreateMessageId(message.ProcessStepId, message.DispatchGeneration)
            };

            if (!batch.TryAddMessage(queueMessage))
            {
                await _sender.SendMessagesAsync(batch, cancellationToken);
                batch.Dispose();
                batch = await _sender.CreateMessageBatchAsync(cancellationToken);
                if (!batch.TryAddMessage(queueMessage))
                {
                    throw new InvalidOperationException("Process-step queue message is too large for a Service Bus batch.");
                }
            }
        }

        if (batch.Count > 0)
        {
            await _sender.SendMessagesAsync(batch, cancellationToken);
        }

        batch.Dispose();
    }
}
