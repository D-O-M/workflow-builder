using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessRealtimeProjectionRepository
{
    Task<ProcessRealtimeSummaryProjectionRecord?> GetSummaryAsync(
        Guid processId,
        CancellationToken cancellationToken);

    Task<bool> TrySaveSummaryAsync(
        ProcessRealtimeSummaryProjectionRecord record,
        bool create,
        CancellationToken cancellationToken);

    Task<ProcessNodeRealtimeProjectionRecord?> GetNodeAsync(
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>> GetNodesAsync(
        Guid processId,
        IReadOnlyCollection<Guid> blueprintNodeIds,
        CancellationToken cancellationToken);

    Task<(bool Created, ProcessNodeRealtimeProjectionRecord Record)> CreateNodeIfNotExistsAsync(
        ProcessNodeRealtimeProjectionRecord record,
        CancellationToken cancellationToken);

    Task<bool> TryUpdateNodeAsync(
        ProcessNodeRealtimeProjectionRecord record,
        CancellationToken cancellationToken);
}
