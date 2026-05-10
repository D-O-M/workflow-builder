using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class PhaseCorrelationRepository
    : CosmosRepository<PhaseCorrelationRecord>,
        IPhaseCorrelationRepository
{
    public PhaseCorrelationRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.TargetPhaseKey))
    {
    }

    public async Task<PhaseCorrelationRecord> CreateAsync(
        PhaseCorrelationRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await base.CreateAsync(record, cancellationToken)).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return await GetAsync(record.Id, record.TargetPhaseKey, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Phase correlation record '{record.Id}' already exists but could not be read.");
        }
    }

    public async Task<PhaseCorrelationRecord?> GetAsync(
        string id,
        string targetPhaseKey,
        CancellationToken cancellationToken) =>
        await GetAsync(
            id,
            new PartitionKey(targetPhaseKey),
            cancellationToken);
}
