using Backend.Plugins.Communication;

namespace Backend.Interfaces.Queues;

public interface IProcessStepQueueWriter
{
    Task EnqueueAsync(
        ProcessStepInitialisationMessage message,
        CancellationToken cancellationToken);
}
