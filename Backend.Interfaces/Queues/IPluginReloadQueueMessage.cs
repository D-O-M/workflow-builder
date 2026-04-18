using Backend.Plugins.Communication;

namespace Backend.Interfaces.Queues;

public interface IPluginReloadQueueMessage
{
    string MessageId { get; }

    DateTimeOffset LockedUntil { get; }

    PluginReloadRequestMessage Payload { get; }

    Task CompleteAsync(CancellationToken cancellationToken);

    Task AbandonAsync(CancellationToken cancellationToken);

    Task RenewLockAsync(CancellationToken cancellationToken);
}
