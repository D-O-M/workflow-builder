using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class ProcessDirectoryRepository
    : CosmosRepository<ProcessDirectoryRecord>,
        IProcessDirectoryRepository
{
    public ProcessDirectoryRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.Id))
    {
    }

    public Task<ProcessDirectoryRecord?> GetByProcessIdAsync(
        Guid processId,
        CancellationToken cancellationToken) =>
        GetAsync(
            processId.ToString(),
            new PartitionKey(processId.ToString()),
            cancellationToken);

    public async Task<ProcessDirectoryRecord> UpsertAsync(
        ProcessDirectoryRecord record,
        CancellationToken cancellationToken) =>
        (await UpsertAsync(
            record,
            cancellationToken,
            options: null)).Resource;
}
