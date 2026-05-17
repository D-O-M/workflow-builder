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
}
