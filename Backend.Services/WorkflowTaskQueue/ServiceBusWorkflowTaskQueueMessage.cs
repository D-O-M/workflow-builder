using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace Backend.Services.WorkflowTaskQueue;

internal sealed class ServiceBusWorkflowTaskQueueMessage : IWorkflowTaskQueueMessage
{
    private readonly ServiceBusReceiver _receiver;
    private readonly ServiceBusReceivedMessage _message;

    public ServiceBusWorkflowTaskQueueMessage(
        ServiceBusReceiver receiver,
        ServiceBusReceivedMessage message)
    {
        _receiver = receiver;
        _message = message;
        Payload = JsonSerializer.Deserialize<WorkflowTaskInitialisationMessage>(_message.Body)
            ?? throw new InvalidOperationException("Unable to deserialize workflow task queue message.");
    }

    public string MessageId => _message.MessageId;

    public DateTimeOffset LockedUntil => _message.LockedUntil;

    public WorkflowTaskInitialisationMessage Payload { get; }

    public Task CompleteAsync(CancellationToken cancellationToken) =>
        _receiver.CompleteMessageAsync(_message, cancellationToken);

    public Task AbandonAsync(CancellationToken cancellationToken) =>
        _receiver.AbandonMessageAsync(_message, cancellationToken: cancellationToken);

    public Task RenewLockAsync(CancellationToken cancellationToken) =>
        _receiver.RenewMessageLockAsync(_message, cancellationToken);
}
