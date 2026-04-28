using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface INodeJoinStateRepository
{
    Task<NodeJoinStateRecord?> GetJoinStateAsync(
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken);

    Task<(bool Created, NodeJoinStateRecord State)> CreateJoinStateIfNotExistsAsync(
        NodeJoinStateRecord state,
        CancellationToken cancellationToken);

    Task<bool> TryUpdateJoinStateAsync(
        NodeJoinStateRecord state,
        CancellationToken cancellationToken);
}
