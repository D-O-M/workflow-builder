namespace Backend.Interfaces.Queues;

public interface IPluginReloadQueueReader
{
    Task<IPluginReloadQueueMessage?> ReceiveAsync(CancellationToken cancellationToken);
}
