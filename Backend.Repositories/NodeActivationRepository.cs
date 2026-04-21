using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class NodeActivationRepository
    : CosmosRepository<NodeActivationRecord>,
    INodeActivationRepository
{
    public NodeActivationRepository(Container container)
        : base(container, state => state.Id, state => new PartitionKey(state.ProcessId))
    {
    }

    public async Task<NodeActivationRecord> UpsertActivationAsync(
        NodeActivationRecord state,
        CancellationToken cancellationToken) =>
        (await UpsertAsync(state, cancellationToken)).Resource;

    public async Task<List<NodeActivationRecord>> GetNodeActivationsAsync(
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken)
    {
        var processIdString = processId.ToString();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.processId = @processId AND c.blueprintNodeId = @blueprintNodeId")
            .WithParameter("@processId", processIdString)
            .WithParameter("@blueprintNodeId", blueprintNodeId.ToString());

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(processIdString)
            });
    }
}
