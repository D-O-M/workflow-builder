using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessStepFinalizationRepository
{
    Task<ProcessStepFinalizationRecord?> GetAsync(
        Guid processStepId,
        CancellationToken cancellationToken);

    Task<(bool Created, ProcessStepFinalizationRecord Record)> CreateIfNotExistsAsync(
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken);

    Task<bool> TryUpdateAsync(
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessStepFinalizationRecord>> GetRecoverableFinalizationsAsync(
        DateTimeOffset now,
        TimeSpan minRecordAge,
        int maxRetryCount,
        int maxItemCount,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<ProcessStepFinalizationRecoveryBacklogSnapshot> GetRecoveryBacklogSnapshotAsync(
        DateTimeOffset now,
        TimeSpan minRecordAge,
        int maxRetryCount,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

public sealed record ProcessStepFinalizationRecoveryBacklogSnapshot(
    long IncompleteCount,
    DateTimeOffset? OldestIncompleteUpdatedAt);
