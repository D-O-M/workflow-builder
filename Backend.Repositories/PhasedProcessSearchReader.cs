using System.Net;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class PhasedProcessSearchReader(CosmosClient client, ProcessStateInitializationOptions options)
    : IPhasedProcessSearchReader
{
    private readonly Container _container = client.GetContainer(options.DatabaseName, options.PhasedProcessesContainerName);

    public async Task<PhasedProcessSearchPage> ReadPageAsync(
        PhasedProcessSearchCriteria criteria, string? continuationToken, int pageSize, CancellationToken cancellationToken)
    {
        var filters = new List<string> { "c.createdAt <= @createdTo" };
        if (criteria.Status is not null)
            filters.Add("c.status = @status");
        if (criteria.DefinitionIds is not null)
            filters.Add("ARRAY_CONTAINS(@definitions, c.phasedBlueprintDefinitionId)");
        if (criteria.ProcessId.HasValue)
            filters.Add("c.id = @id");

        var query = new QueryDefinition($"""
            SELECT c.id, c.phasedBlueprintDefinitionId, c.phasedBlueprintVersionId, c.createdAt, c.updatedAt,
                   c.status, c.currentPhaseId,
                   ARRAY(SELECT p.phaseId, p.phaseIndex, p.phaseName, p.status FROM p IN c.phases) AS phases
            FROM c WHERE {string.Join(" AND ", filters)}
            ORDER BY c.createdAt DESC
            """)
            .WithParameter("@createdTo", criteria.CreatedTo);
        if (criteria.Status is not null)
            query.WithParameter("@status", criteria.Status);
        if (criteria.DefinitionIds is not null)
            query.WithParameter("@definitions", criteria.DefinitionIds);
        if (criteria.ProcessId is { } processId)
            query.WithParameter("@id", processId.ToString());

        var request = new QueryRequestOptions { MaxItemCount = pageSize, ResponseContinuationTokenLimitInKb = 1 };
        if (criteria.DefinitionIds is [var onlyDefinition])
            request.PartitionKey = new PartitionKey(onlyDefinition);

        try
        {
            using var feed = _container.GetItemQueryIterator<PhasedProcessSearchRow>(query, continuationToken, request);
            while (feed.HasMoreResults)
            {
                var page = await feed.ReadNextAsync(cancellationToken);
                if (page.Count > 0 || page.ContinuationToken is null)
                    return new(page.ToList(), page.ContinuationToken);
            }
            return new([], null);
        }
        catch (CosmosException exception) when (continuationToken is not null && exception.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidDataException("Invalid Phase Series search cursor.", exception);
        }
    }
}
