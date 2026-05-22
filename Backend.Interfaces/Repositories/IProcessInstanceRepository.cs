using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessInstanceRepository<TGlobalState>
{
    Task<ProcessInstanceRecord<TGlobalState>> CreateProcessState(
        ProcessInstanceRecord<TGlobalState> state,
        CancellationToken cancellationToken);

    Task<ProcessInstanceRecord<TGlobalState>> GetProcessStateById(
        Guid processId,
        int blueprintVersionId,
        CancellationToken cancellationToken);

    Task<List<ProcessInstanceRecord<TGlobalState>>> GetProcessStatesForBlueprint(
        int blueprintVersionId,
        CancellationToken cancellationToken);

    Task<ProcessStateSummary?> GetLatestProcessStateForBlueprint(
        int blueprintVersionId,
        CancellationToken cancellationToken);

    Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
        int blueprintVersionId,
        string? continuationToken,
        int maxItemCount,
        CancellationToken cancellationToken);

    Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
        int blueprintVersionId,
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessInstanceRecord<TGlobalState>>> GetRecentProcessStatesForReconciliationAsync(
        DateTimeOffset createdFrom,
        int maxItemCount,
        IReadOnlyCollection<ProcessStatus> statuses,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<ProcessInstanceRecord<TGlobalState>> UpdateProcessState(
        ProcessInstanceRecord<TGlobalState> state,
        CancellationToken cancellationToken);

    Task<bool> TryUpdateProcessState(
        ProcessInstanceRecord<TGlobalState> state,
        CancellationToken cancellationToken);
}
