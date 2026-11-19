using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class PhasedProcessRepository
    : CosmosRepository<PhasedProcessRecord>,
        IPhasedProcessRepository
{
    public PhasedProcessRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.PhasedBlueprintDefinitionId))
    {
    }

    public async Task<PhasedProcessRecord> CreateAsync(
        PhasedProcessRecord record,
        CancellationToken cancellationToken) =>
        (await base.CreateAsync(record, cancellationToken)).Resource;

    public async Task<PhasedProcessRecord> GetAsync(
        Guid phasedProcessId,
        int phasedBlueprintDefinitionId,
        CancellationToken cancellationToken) =>
        await GetAsync(phasedProcessId, phasedBlueprintDefinitionId.ToString(), cancellationToken);

    public async Task<PhasedProcessRecord> GetAsync(
        Guid phasedProcessId,
        string phasedBlueprintDefinitionId,
        CancellationToken cancellationToken)
    {
        var record = await GetAsync(
            phasedProcessId.ToString(),
            new PartitionKey(phasedBlueprintDefinitionId),
            cancellationToken);

        if (record is null)
        {
            throw new EntityNotFoundException<PhasedProcessRecord>(phasedProcessId.ToString());
        }

        return record;
    }

    public async Task<PhasedProcessOverviewState> GetOverviewAsync(
        Guid phasedProcessId, int phasedBlueprintDefinitionId, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition("""
            SELECT c.phasedBlueprintVersionId, c.status, c.createdAt, c.updatedAt, c.currentPhaseId,
                   ARRAY(SELECT p.phaseId, p.phaseIndex, p.phaseName, p.blueprintDefinitionId, p.blueprintVersionId,
                                p.childProcessId, p.status, p.startedAt, p.completedAt, p.failureMessage
                         FROM p IN c.phases) AS phases
            FROM c WHERE c.id = @id
            """).WithParameter("@id", phasedProcessId.ToString());
        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(phasedBlueprintDefinitionId.ToString()) };
        using var feed = Container.GetItemQueryIterator<PhasedProcessOverviewState>(query, requestOptions: options);
        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(cancellationToken);
            if (page.Count > 0)
                return page.First();
        }

        throw new EntityNotFoundException<PhasedProcessRecord>(phasedProcessId.ToString());
    }

    public async Task<PhasedProcessRecord> UpdateAsync(
        PhasedProcessRecord record,
        CancellationToken cancellationToken) =>
        (await ReplaceAsync(record, cancellationToken, record.ETag)).Resource;

    public async Task<bool> TryUpdateAsync(
        PhasedProcessRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdateAsync(record, cancellationToken);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}
