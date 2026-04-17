namespace Backend.Interfaces.Queues;

public interface IWorkflowTaskQueueReader
{
    Task<IWorkflowTaskQueueMessage?> ReceiveAsync(CancellationToken cancellationToken);
}
