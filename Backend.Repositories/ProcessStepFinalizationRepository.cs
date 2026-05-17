using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class ProcessStepFinalizationRepository
    : CosmosRepository<ProcessStepFinalizationRecord>,
      IProcessStepFinalizationRepository
{
    public ProcessStepFinalizationRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.Id))
    {
    }

    public Task<ProcessStepFinalizationRecord?> GetAsync(
        Guid processStepId,
        CancellationToken cancellationToken) =>
        GetAsync(
            processStepId.ToString(),
            new PartitionKey(processStepId.ToString()),
            cancellationToken);

    public async Task<(bool Created, ProcessStepFinalizationRecord Record)> CreateIfNotExistsAsync(
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, (await CreateAsync(record, cancellationToken)).Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetAsync(
                Guid.Parse(record.Id),
                cancellationToken);
            return (false, existing ?? throw new InvalidOperationException(
                $"Finalization record '{record.Id}' already exists but could not be read."));
        }
    }

    public async Task<bool> TryUpdateAsync(
        ProcessStepFinalizationRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReplaceAsync(record, cancellationToken, record.ETag);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}
