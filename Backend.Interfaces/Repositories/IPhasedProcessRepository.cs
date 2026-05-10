using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IPhasedProcessRepository
{
    Task<PhasedProcessRecord> CreateAsync(PhasedProcessRecord record, CancellationToken cancellationToken);

    Task<PhasedProcessRecord> GetAsync(Guid phasedProcessId, int phasedBlueprintDefinitionId, CancellationToken cancellationToken);

    Task<PhasedProcessRecord> GetAsync(Guid phasedProcessId, string phasedBlueprintDefinitionId, CancellationToken cancellationToken);

    Task<PhasedProcessRecord> UpdateAsync(PhasedProcessRecord record, CancellationToken cancellationToken);

    Task<bool> TryUpdateAsync(PhasedProcessRecord record, CancellationToken cancellationToken);
}

