using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessStepRepository
{
    Task<ProcessStepRecord> CreateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken);

    Task<ProcessStepRecord> GetProcessStepById(
        Guid taskInstanceId,
        Guid processId,
        CancellationToken cancellationToken);

    Task<List<ProcessStepRecord>> GetProcessStepsForProcess(
        Guid processId,
        CancellationToken cancellationToken);

    Task<ProcessStepRecord> UpdateProcessStep(
        ProcessStepRecord state,
        CancellationToken cancellationToken);
}
