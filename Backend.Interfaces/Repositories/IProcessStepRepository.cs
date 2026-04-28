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

    Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
        Guid processId,
        CancellationToken cancellationToken);

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
