namespace Backend.Interfaces.Queues;

public interface IProcessStepQueueProcessor
{
    Task StartProcessingAsync(
        Func<IProcessStepQueueMessage, CancellationToken, Task> processMessageAsync,
        CancellationToken cancellationToken);

    Task StopProcessingAsync(CancellationToken cancellationToken);
}
