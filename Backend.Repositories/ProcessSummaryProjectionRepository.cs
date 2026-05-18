using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class ProcessSummaryProjectionRepository
    : CosmosRepository<ProcessSummaryProjectionRecord>,
        IProcessSummaryProjectionRepository
{
    public ProcessSummaryProjectionRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.PartitionKey))
    {
    }

    public Task<ProcessSummaryProjectionRecord?> GetByIdAsync(
        string id,
        string partitionKey,
        CancellationToken cancellationToken) =>
        GetAsync(
            id,
            new PartitionKey(partitionKey),
            cancellationToken);

    public async Task<ProcessSummaryProjectionRecord> UpsertAsync(
        ProcessSummaryProjectionRecord record,
        CancellationToken cancellationToken) =>
        (await UpsertAsync(
            record,
            cancellationToken,
            options: null)).Resource;

    public async Task<ProcessSummaryProjectionPage> QueryRecentAsync(
        ProcessSummaryProjectionScope scope,
        string tenantId,
        int? blueprintDefinitionId,
        IReadOnlyCollection<int>? blueprintVersionIds,
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken)
    {
        var clauses = new List<string>
        {
            "c.tenantId = @tenantId",
            "c.scope = @scope"
        };

        if (blueprintDefinitionId.HasValue)
        {
            clauses.Add("c.blueprintDefinitionId = @blueprintDefinitionId");
        }

        if (blueprintVersionIds is { Count: > 0 })
        {
            clauses.Add("ARRAY_CONTAINS(@blueprintVersionIds, c.blueprintVersionId)");
        }

        if (createdFrom.HasValue)
        {
            clauses.Add("c.createdAt >= @createdFrom");
        }

        if (createdTo.HasValue)
        {
            clauses.Add("c.createdAt <= @createdTo");
        }

        if (statuses is { Count: > 0 })
        {
            clauses.Add("ARRAY_CONTAINS(@statuses, c.status)");
        }

        var query = new QueryDefinition(
                $"SELECT * FROM c WHERE {string.Join(" AND ", clauses)} ORDER BY c.createdAt DESC")
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@scope", scope.ToString());

        if (blueprintDefinitionId.HasValue)
        {
            query = query.WithParameter("@blueprintDefinitionId", blueprintDefinitionId.Value);
        }

        if (blueprintVersionIds is { Count: > 0 })
        {
            query = query.WithParameter("@blueprintVersionIds", blueprintVersionIds.ToArray());
        }

        if (createdFrom.HasValue)
        {
            query = query.WithParameter("@createdFrom", createdFrom.Value);
        }

        if (createdTo.HasValue)
        {
            query = query.WithParameter("@createdTo", createdTo.Value);
        }

        if (statuses is { Count: > 0 })
        {
            query = query.WithParameter("@statuses", statuses.Select(status => status.ToString()).ToArray());
        }

        using var iterator = Container.GetItemQueryIterator<ProcessSummaryProjectionRecord>(
            query,
            continuationToken,
            new QueryRequestOptions
            {
                MaxItemCount = Math.Clamp(maxItemCount, 1, 250),
            });

        if (!iterator.HasMoreResults)
        {
            return new ProcessSummaryProjectionPage
            {
                Items = [],
                ContinuationToken = null,
            };
        }

        var response = await iterator.ReadNextAsync(cancellationToken);
        return new ProcessSummaryProjectionPage
        {
            Items = response.Resource.ToList(),
            ContinuationToken = response.ContinuationToken,
        };
    }
}
