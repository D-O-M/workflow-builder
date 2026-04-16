using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public class WorkflowInstanceRepository<TGlobalState>
    : CosmosRepository<WorkflowObject<TGlobalState>>,
    IWorkflowInstanceRepository<TGlobalState>
{
    public WorkflowInstanceRepository(Container container)
        : base(container, wo => wo.WorkflowInstanceId, wo => new PartitionKey(wo.BlueprintId))
    {}

    public async Task<WorkflowObject<TGlobalState>> CreateWorkflowState(
        WorkflowObject<TGlobalState> state,
        CancellationToken cancellationToken)
    {
        return (await CreateAsync(state, cancellationToken)).Resource;
    }

    public async Task<WorkflowObject<TGlobalState>> GetWorkflowStateById(
        Guid workflowInstanceId,
        int workflowBlueprintId,
        CancellationToken cancellationToken)
    {
        var workflowState = await GetAsync(
            workflowInstanceId.ToString(),
            new PartitionKey(workflowBlueprintId.ToString()),
            cancellationToken);

        if (workflowState is null)
        {
            throw new EntityNotFoundException<WorkflowObject<TGlobalState>>(
                workflowInstanceId.ToString());
        }

        return workflowState;
    }

    public async Task<WorkflowObject<TGlobalState>> UpdateWorkflowState(
        WorkflowObject<TGlobalState> state,
        CancellationToken cancellationToken)
    {
        return (await ReplaceAsync(state, cancellationToken)).Resource;
    }
}
