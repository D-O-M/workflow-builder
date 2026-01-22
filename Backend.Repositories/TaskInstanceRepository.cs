using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public class TaskInstanceRepository
    : CosmosRepository<TaskObject>,
    ITaskInstanceRepository
{
    public TaskInstanceRepository(Container container)
        : base(container, wo => wo.Id, wo => new PartitionKey(wo.WorkflowInstanceId))
    { }

    public async Task<TaskObject> GetTaskInstanceById(
        Guid taskInstanceId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var taskInstance = await GetAsync(
            taskInstanceId.ToString(),
            new PartitionKey(workflowInstanceId.ToString()),
            cancellationToken);

        if (taskInstance is null)
        {
            throw new EntityNotFoundException<TaskObject>(
                workflowInstanceId.ToString());
        }

        return taskInstance;
    }

    public Task<TaskObject> UpdateTaskInstance(
        TaskObject state, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
