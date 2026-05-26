using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessInstanceRepository
{
    Task<ProcessInstanceRecord> CreateProcessState(
        ProcessInstanceRecord state,
        CancellationToken cancellationToken);

    Task<ProcessInstanceRecord> GetProcessStateById(
        Guid processId,
        int blueprintVersionId,
        CancellationToken cancellationToken);

    Task<List<ProcessInstanceRecord>> GetProcessStatesForBlueprint(
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

    Task<IReadOnlyList<ProcessInstanceRecord>> GetRecentProcessStatesForReconciliationAsync(
        DateTimeOffset createdFrom,
        int maxItemCount,
        IReadOnlyCollection<ProcessStatus> statuses,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    Task<ProcessInstanceRecord> UpdateProcessState(
        ProcessInstanceRecord state,
        CancellationToken cancellationToken);

    Task<bool> TryUpdateProcessState(
        ProcessInstanceRecord state,
        CancellationToken cancellationToken);
}
