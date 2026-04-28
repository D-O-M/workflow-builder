using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class NodeJoinStateRepository
    : CosmosRepository<NodeJoinStateRecord>,
    INodeJoinStateRepository
{
    public NodeJoinStateRepository(Container container)
        : base(container, state => state.Id, state => new PartitionKey(state.ProcessId))
    {
    }

    public async Task<NodeJoinStateRecord?> GetJoinStateAsync(
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken) =>
        await GetAsync(
            blueprintNodeId.ToString(),
            new PartitionKey(processId.ToString()),
            cancellationToken);

    public async Task<(bool Created, NodeJoinStateRecord State)> CreateJoinStateIfNotExistsAsync(
        NodeJoinStateRecord state,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, (await CreateAsync(state, cancellationToken)).Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetJoinStateAsync(
                Guid.Parse(state.ProcessId),
                Guid.Parse(state.BlueprintNodeId),
                cancellationToken);
            return (false, existing ?? throw new InvalidOperationException(
                $"Join state '{state.BlueprintNodeId}' for process '{state.ProcessId}' already exists but could not be read."));
        }
    }

    public async Task<bool> TryUpdateJoinStateAsync(
        NodeJoinStateRecord state,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReplaceAsync(state, cancellationToken, state.ETag);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}
