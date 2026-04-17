using Backend.Plugins.Communication;

namespace Backend.Interfaces.Queues;

public interface IWorkflowTaskQueueWriter
{
    Task EnqueueAsync(
        WorkflowTaskInitialisationMessage message,
        CancellationToken cancellationToken);
}
