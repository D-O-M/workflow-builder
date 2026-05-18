using Backend.Database.State.Objects;

namespace Backend.Interfaces.Repositories;

public interface IProcessDirectoryRepository
{
    Task<ProcessDirectoryRecord?> GetByProcessIdAsync(
        Guid processId,
        CancellationToken cancellationToken);

    Task<ProcessDirectoryRecord> UpsertAsync(
        ProcessDirectoryRecord record,
        CancellationToken cancellationToken);
}
