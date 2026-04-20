using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace Backend.Services.ProcessStepQueue;

internal sealed class ServiceBusProcessStepQueueMessage : IProcessStepQueueMessage
{
    private readonly ServiceBusReceiver _receiver;
    private readonly ServiceBusReceivedMessage _message;

    public ServiceBusProcessStepQueueMessage(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message)
    {
        _receiver = receiver;
        _message = message;
        Payload = JsonSerializer.Deserialize<ProcessStepInitialisationMessage>(_message.Body)
            ?? throw new InvalidOperationException("Unable to deserialize process step queue message.");
    }

    public string MessageId => _message.MessageId;

    public DateTimeOffset LockedUntil => _message.LockedUntil;

    public ProcessStepInitialisationMessage Payload { get; }

    public Task CompleteAsync(CancellationToken cancellationToken) =>
        _receiver.CompleteMessageAsync(_message, cancellationToken);

    public Task AbandonAsync(CancellationToken cancellationToken) =>
        _receiver.AbandonMessageAsync(_message, cancellationToken: cancellationToken);

    public Task RenewLockAsync(CancellationToken cancellationToken) =>
        _receiver.RenewMessageLockAsync(_message, cancellationToken);
}
