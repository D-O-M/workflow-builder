using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public class ProcessInstanceRepository<TGlobalState>
    : CosmosRepository<ProcessInstanceRecord<TGlobalState>>,
    IProcessInstanceRepository<TGlobalState>
{
    public ProcessInstanceRepository(Container container)
        : base(container, wo => wo.Id, wo => new PartitionKey(wo.BlueprintId))
    {}

    public async Task<ProcessInstanceRecord<TGlobalState>> CreateProcessState(
        ProcessInstanceRecord<TGlobalState> state,
        CancellationToken cancellationToken)
    {
        return (await CreateAsync(state, cancellationToken)).Resource;
    }

    public async Task<ProcessInstanceRecord<TGlobalState>> GetProcessStateById(
        Guid processId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var processState = await GetAsync(
            processId.ToString(),
            new PartitionKey(blueprintVersionId.ToString()),
            cancellationToken);

        if (processState is null)
        {
            throw new EntityNotFoundException<ProcessInstanceRecord<TGlobalState>>(
                processId.ToString());
        }

        return processState;
    }

    public async Task<List<ProcessInstanceRecord<TGlobalState>>> GetProcessStatesForBlueprint(
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.blueprintId = @blueprintId ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", blueprintVersionId.ToString());

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(blueprintVersionId.ToString())
            });
    }

    public async Task<ProcessStateSummary?> GetLatestProcessStateForBlueprint(
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 c.id, c.blueprintId, c.createdAt, c.updatedAt, c.status " +
            "FROM c WHERE c.blueprintId = @blueprintId ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", blueprintVersionId.ToString());

        using var iterator = Container.GetItemQueryIterator<ProcessStateSummary>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(blueprintVersionId.ToString()),
                MaxItemCount = 1,
            });

        if (!iterator.HasMoreResults)
        {
            return null;
        }

        var response = await iterator.ReadNextAsync(cancellationToken);
        return response.Resource.FirstOrDefault();
    }

    public async Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
        int blueprintVersionId,
        string? continuationToken,
        int maxItemCount,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT c.id, c.blueprintId, c.createdAt, c.updatedAt, c.status " +
            "FROM c WHERE c.blueprintId = @blueprintId ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", blueprintVersionId.ToString());

        using var iterator = Container.GetItemQueryIterator<ProcessStateSummary>(
            query,
            continuationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(blueprintVersionId.ToString()),
                MaxItemCount = maxItemCount,
            });

        if (!iterator.HasMoreResults)
        {
            return new ProcessStateSummaryPage
            {
                Items = [],
                ContinuationToken = null,
            };
        }

        var response = await iterator.ReadNextAsync(cancellationToken);
        return new ProcessStateSummaryPage
        {
            Items = response.Resource.ToList(),
            ContinuationToken = response.ContinuationToken,
        };
    }

    public async Task<ProcessInstanceRecord<TGlobalState>> UpdateProcessState(
        ProcessInstanceRecord<TGlobalState> state,
        CancellationToken cancellationToken)
    {
        return (await ReplaceAsync(state, cancellationToken)).Resource;
    }
}
