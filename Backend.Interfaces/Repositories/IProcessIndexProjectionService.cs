using System.Text.Json;
using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessIndexProjectionService
{
    Task UpsertFromProcessAsync(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        CancellationToken cancellationToken);

    Task<ProcessDirectoryRecord?> GetDirectoryByProcessIdAsync(
        Guid processId,
        CancellationToken cancellationToken);

    Task<ProcessSummaryProjectionPage> GetRecentTenantSummariesAsync(
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken);

    Task<ProcessSummaryProjectionPage> GetRecentBlueprintSummariesAsync(
        int blueprintDefinitionId,
        IReadOnlyCollection<int>? blueprintVersionIds,
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken);
}
