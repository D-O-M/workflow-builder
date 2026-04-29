using Backend.Plugins.Communication;

namespace Backend.Interfaces.Queues;

public interface IProcessStepQueueWriter
{
    Task EnqueueAsync(
        ProcessStepInitialisationMessage message,
        CancellationToken cancellationToken);

    async Task EnqueueBatchAsync(
        IReadOnlyCollection<ProcessStepInitialisationMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            await EnqueueAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }
}
