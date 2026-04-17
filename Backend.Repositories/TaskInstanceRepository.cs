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

    public async Task<TaskObject> CreateTaskInstance(
        TaskObject state,
        CancellationToken cancellationToken)
    {
        return (await CreateAsync(state, cancellationToken)).Resource;
    }

    public async Task<List<TaskObject>> GetTaskInstancesForWorkflow(
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var workflowInstanceIdString = workflowInstanceId.ToString();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.workflowId = @workflowId ORDER BY c.createdAt ASC")
            .WithParameter("@workflowId", workflowInstanceIdString);

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(workflowInstanceIdString)
            });
    }

    public async Task<TaskObject> UpdateTaskInstance(
        TaskObject state,
        CancellationToken cancellationToken)
    {
        return (await ReplaceAsync(state, cancellationToken)).Resource;
    }
}
