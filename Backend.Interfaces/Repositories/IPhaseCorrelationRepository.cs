using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IPhaseCorrelationRepository
{
    Task<PhaseCorrelationRecord> CreateAsync(PhaseCorrelationRecord record, CancellationToken cancellationToken);

    Task<PhaseCorrelationRecord?> GetAsync(string id, string targetPhaseKey, CancellationToken cancellationToken);
}

