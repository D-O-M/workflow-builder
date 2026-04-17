using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface ITaskInstanceRepository
{
    Task<TaskObject> CreateTaskInstance(
        TaskObject state,
        CancellationToken cancellationToken);

    Task<TaskObject> GetTaskInstanceById(
        Guid taskInstanceId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken);

    Task<List<TaskObject>> GetTaskInstancesForWorkflow(
        Guid workflowInstanceId,
        CancellationToken cancellationToken);

    Task<TaskObject> UpdateTaskInstance(
        TaskObject state,
        CancellationToken cancellationToken);
}
