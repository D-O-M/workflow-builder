using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessStepDispatchIntentRepository
{
    Task<ProcessStepDispatchIntentRecord?> GetIntentAsync(
        string id,
        string partitionKey,
        CancellationToken cancellationToken);

    Task<(bool Created, ProcessStepDispatchIntentRecord Record)> CreateIntentIfNotExistsAsync(
        ProcessStepDispatchIntentRecord record,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessStepDispatchIntentRecord>> GetDueIntentsAsync(
        long dueBucket,
        int shard,
        int maxItemCount,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<(bool Claimed, ProcessStepDispatchIntentRecord? Record)> TryClaimIntentAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task<bool> MarkIntentDispatchedAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset dispatchedAt,
        CancellationToken cancellationToken);

    Task<bool> MarkIntentRetryableErrorAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        ProcessStepDispatchErrorKind errorKind,
        string errorMessage,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken);

    Task<bool> MarkIntentSkippedTerminalAsync(
        ProcessStepDispatchIntentRecord record,
        string leaseId,
        DateTimeOffset skippedAt,
        CancellationToken cancellationToken);

    Task<ProcessStepDispatchCursorRecord> GetOrCreateCursorAsync(
        int shard,
        long initialDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken);

    Task<bool> TryAdvanceCursorAsync(
        ProcessStepDispatchCursorRecord cursor,
        long nextDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken);

    Task<bool> TryRewindCursorAsync(
        int shard,
        long nextDueBucket,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<ProcessStepDispatchBacklogSnapshot> GetBacklogSnapshotAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
