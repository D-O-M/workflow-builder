using Backend.Plugins.Communication;

namespace Backend.Interfaces.Queues;

public interface IPluginReloadQueueWriter
{
    Task EnqueueAsync(
        PluginReloadRequestMessage message,
        CancellationToken cancellationToken);
}
