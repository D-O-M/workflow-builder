using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Plugins.Communication;

namespace Backend.Services.ProcessStepQueue;

internal sealed class ServiceBusProcessStepQueueMessage : IProcessStepQueueMessage
{
    private readonly object _sync = new();
    private readonly ProcessMessageEventArgs _args;
    private readonly ServiceBusReceivedMessage _message;
    private DateTimeOffset _lockedUntil;

    public ServiceBusProcessStepQueueMessage(ProcessMessageEventArgs args)
    {
        _args = args;
        _message = args.Message;
        _lockedUntil = _message.LockedUntil;
        Payload = JsonSerializer.Deserialize<ProcessStepInitialisationMessage>(_message.Body)
            ?? throw new InvalidOperationException("Unable to deserialize process step queue message.");
    }

    public string MessageId => _message.MessageId;

    public DateTimeOffset EnqueuedAt => _message.EnqueuedTime;

    public DateTimeOffset LockedUntil
    {
        get
        {
            lock (_sync)
            {
                return _lockedUntil;
            }
        }
    }

    public ProcessStepInitialisationMessage Payload { get; }

    public Task CompleteAsync(CancellationToken cancellationToken) =>
        _args.CompleteMessageAsync(_message, cancellationToken);

    public Task AbandonAsync(CancellationToken cancellationToken) =>
        _args.AbandonMessageAsync(_message, cancellationToken: cancellationToken);

    public async Task<DateTimeOffset> RenewLockAsync(CancellationToken cancellationToken)
    {
        await _args.RenewMessageLockAsync(_message, cancellationToken);
        lock (_sync)
        {
            _lockedUntil = _message.LockedUntil;
            return _lockedUntil;
        }
    }
}
