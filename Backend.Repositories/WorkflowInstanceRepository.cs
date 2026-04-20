using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public class WorkflowInstanceRepository<TGlobalState>
    : CosmosRepository<WorkflowObject<TGlobalState>>,
    IWorkflowInstanceRepository<TGlobalState>
{
    public WorkflowInstanceRepository(Container container)
        : base(container, wo => wo.Id, wo => new PartitionKey(wo.BlueprintId))
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

    public async Task<List<WorkflowObject<TGlobalState>>> GetWorkflowStatesForBlueprint(
        int workflowBlueprintId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.blueprintId = @blueprintId ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", workflowBlueprintId.ToString());

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(workflowBlueprintId.ToString())
            });
    }

    public async Task<WorkflowStateSummary?> GetLatestWorkflowStateForBlueprint(
        int workflowBlueprintId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 c.id, c.blueprintId, c.createdAt, c.updatedAt, c.status " +
            "FROM c WHERE c.blueprintId = @blueprintId ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", workflowBlueprintId.ToString());

        using var iterator = Container.GetItemQueryIterator<WorkflowStateSummary>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(workflowBlueprintId.ToString()),
                MaxItemCount = 1,
            });

        if (!iterator.HasMoreResults)
        {
            return null;
        }

        var response = await iterator.ReadNextAsync(cancellationToken);
        return response.Resource.FirstOrDefault();
    }

    public async Task<WorkflowStateSummaryPage> GetWorkflowStateSummaryPageForBlueprint(
        int workflowBlueprintId,
        string? continuationToken,
        int maxItemCount,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT c.id, c.blueprintId, c.createdAt, c.updatedAt, c.status " +
            "FROM c WHERE c.blueprintId = @blueprintId ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", workflowBlueprintId.ToString());

        using var iterator = Container.GetItemQueryIterator<WorkflowStateSummary>(
            query,
            continuationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(workflowBlueprintId.ToString()),
                MaxItemCount = maxItemCount,
            });

        if (!iterator.HasMoreResults)
        {
            return new WorkflowStateSummaryPage
            {
                Items = [],
                ContinuationToken = null,
            };
        }

        var response = await iterator.ReadNextAsync(cancellationToken);
        return new WorkflowStateSummaryPage
        {
            Items = response.Resource.ToList(),
            ContinuationToken = response.ContinuationToken,
        };
    }

    public async Task<WorkflowObject<TGlobalState>> UpdateWorkflowState(
        WorkflowObject<TGlobalState> state,
        CancellationToken cancellationToken)
    {
        return (await ReplaceAsync(state, cancellationToken)).Resource;
    }
}
