using Backend.Plugins.Communication;

namespace Backend.Interfaces.Queues;

public interface IProcessStepQueueMessage
{
    string MessageId { get; }

    DateTimeOffset EnqueuedAt { get; }

    DateTimeOffset LockedUntil { get; }

    ProcessStepInitialisationMessage Payload { get; }

    Task CompleteAsync(CancellationToken cancellationToken);

    Task AbandonAsync(CancellationToken cancellationToken);

    Task RenewLockAsync(CancellationToken cancellationToken);
}
