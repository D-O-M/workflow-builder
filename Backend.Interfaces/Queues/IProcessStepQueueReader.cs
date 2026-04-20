namespace Backend.Interfaces.Queues;

public interface IProcessStepQueueReader
{
    Task<IProcessStepQueueMessage?> ReceiveAsync(CancellationToken cancellationToken);
}
