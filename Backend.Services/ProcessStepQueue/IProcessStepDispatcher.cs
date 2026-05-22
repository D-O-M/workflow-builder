using Backend.Database.State.Objects;

namespace Backend.Services.ProcessStepQueue;

public interface IProcessStepDispatcher
{
    void EnsureDispatchAvailableForExecution();

    Task<ProcessStepDispatchIntentRecord> EnsureDispatchIntentAsync(
        ProcessStepRecord step,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken);

    Task<bool> TryDispatchAsync(
        ProcessStepRecord step,
        CancellationToken cancellationToken);

    Task<int> DrainDueDispatchesAsync(
        int maxItemCount,
        CancellationToken cancellationToken);

    Task RefreshBacklogMetricsAsync(CancellationToken cancellationToken);
}
