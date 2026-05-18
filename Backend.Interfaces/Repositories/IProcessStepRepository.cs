using System.Text.Json;
using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessStepRepository
{
    Task<ProcessStepRecord> CreateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken);

    Task<(bool Created, ProcessStepRecord State)> CreateProcessStepIfNotExists(
        ProcessStepRecord state,
        CancellationToken cancellationToken);

    async Task<IReadOnlyList<(bool Created, ProcessStepRecord State)>> CreateProcessStepsIfNotExists(
        IReadOnlyCollection<ProcessStepRecord> states,
        CancellationToken cancellationToken)
    {
        var results = new List<(bool Created, ProcessStepRecord State)>(states.Count);
        foreach (var state in states)
        {
            results.Add(await CreateProcessStepIfNotExists(state, cancellationToken));
        }

        return results;
    }

    Task<ProcessStepRecord> GetProcessStepById(
        Guid taskInstanceId,
        Guid processId,
        CancellationToken cancellationToken);

    Task<(bool Claimed, ProcessStepRecord? State)> TryClaimProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        int taskDefinitionId,
        int allowedMajor,
        int allowedMinor,
        int resolvedTaskVersionId,
        string resolvedTaskVersion,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    Task<bool> TryResetProcessStepToPendingAsync(
        Guid taskInstanceId,
        Guid processId,
        DateTimeOffset resetAt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
        Guid processId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessStepRecord>> GetDueStepDispatchesAsync(
        DateTimeOffset now,
        int maxItemCount,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<ProcessStepDispatchBacklogSnapshot> GetDispatchBacklogSnapshotAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<(bool Claimed, ProcessStepRecord? State)> TryClaimStepDispatchAsync(
        ProcessStepRecord state,
        string leaseId,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<bool> MarkStepDispatchSucceededAsync(
        ProcessStepRecord state,
        string leaseId,
        DateTimeOffset dispatchedAt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<bool> MarkStepDispatchRetryableErrorAsync(
        ProcessStepRecord state,
        string leaseId,
        ProcessStepDispatchErrorKind errorKind,
        string errorMessage,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<bool> MarkStepDispatchSkippedTerminalAsync(
        ProcessStepRecord state,
        string leaseId,
        DateTimeOffset skippedAt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task CompleteProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        Dictionary<string, JsonElement>? effectiveInputData,
        Dictionary<string, JsonElement> outputData,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task FailProcessStepAsync(
        Guid taskInstanceId,
        Guid processId,
        Dictionary<string, JsonElement>? effectiveInputData,
        string? errorMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken);

    Task<ProcessStepRecord> UpdateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken);

    Task<bool> TryUpdateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken);
}

public sealed record ProcessStepDispatchBacklogSnapshot(
    long PendingCount,
    DateTimeOffset? OldestPendingCreatedAt);
