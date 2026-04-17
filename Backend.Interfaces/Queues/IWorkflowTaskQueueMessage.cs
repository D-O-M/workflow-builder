using Backend.Plugins.Communication;

namespace Backend.Interfaces.Queues;

public interface IWorkflowTaskQueueMessage
{
    string MessageId { get; }

    DateTimeOffset LockedUntil { get; }

    WorkflowTaskInitialisationMessage Payload { get; }

    Task CompleteAsync(CancellationToken cancellationToken);

    Task AbandonAsync(CancellationToken cancellationToken);

    Task RenewLockAsync(CancellationToken cancellationToken);
}
