using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface INodeActivationRepository
{
    Task<NodeActivationRecord> UpsertActivationAsync(
        NodeActivationRecord state,
        CancellationToken cancellationToken);

    Task<List<NodeActivationRecord>> GetNodeActivationsAsync(
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken);
}
