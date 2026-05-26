using System.Text.Json;
using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessRealtimeProjectionService
{
    Task UpsertFromProcessAsync(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        CancellationToken cancellationToken);

    Task UpsertFromStepAsync(
        ProcessStepRecord step,
        int blueprintDefinitionId,
        int blueprintVersionId,
        CancellationToken cancellationToken);

    Task UpsertFromProcessAndStepsAsync(
        ProcessInstanceRecord process,
        int blueprintDefinitionId,
        int blueprintVersionId,
        IReadOnlyCollection<ProcessStepRecord> steps,
        CancellationToken cancellationToken);

    Task<ProcessRealtimeSummaryProjectionRecord?> GetSummaryAsync(
        Guid processId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>> GetNodeProjectionsAsync(
        Guid processId,
        IReadOnlyCollection<Guid> blueprintNodeIds,
        CancellationToken cancellationToken);
}
