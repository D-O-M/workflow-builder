using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Repositories.Processes;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class ProcessSummaryProjectionRepository
    : CosmosRepository<ProcessSummaryProjectionRecord>,
        IProcessSummaryProjectionRepository
{
    private readonly ProcessSummaryProjectionQueryEngine _queryEngine;

    public ProcessSummaryProjectionRepository(Container container)
        : base(container, record => record.Id, record => new PartitionKey(record.PartitionKey))
    {
        _queryEngine = new ProcessSummaryProjectionQueryEngine(
            new CosmosProcessSummaryProjectionPartitionReader(container));
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

    public Task<ProcessSummaryProjectionPage> QueryRecentAsync(
        ProcessSummaryProjectionScope scope,
        string tenantId,
        int? blueprintDefinitionId,
        IReadOnlyCollection<int>? blueprintVersionIds,
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken) =>
        _queryEngine.QueryRecentAsync(
            scope,
            tenantId,
            blueprintDefinitionId,
            blueprintVersionIds,
            continuationToken,
            maxItemCount,
            createdFrom,
            createdTo,
            statuses,
            cancellationToken);

    private sealed class CosmosProcessSummaryProjectionPartitionReader : IProcessSummaryProjectionPartitionReader
    {
        private readonly Container _container;

        public CosmosProcessSummaryProjectionPartitionReader(Container container)
        {
            _container = container;
        }

        public async Task<ProcessSummaryProjectionPartitionPage> QueryPartitionAsync(
            string partitionKey,
            string? continuationToken,
            int maxItemCount,
            int? blueprintDefinitionId,
            DateTimeOffset? createdFrom,
            DateTimeOffset createdTo,
            IReadOnlyCollection<ProcessStatus>? statuses,
            CancellationToken cancellationToken)
        {
            var clauses = new List<string>
            {
                "c.partitionKey = @partitionKey",
                "c.createdAt <= @createdTo"
            };

            if (blueprintDefinitionId.HasValue)
            {
                clauses.Add("c.blueprintDefinitionId = @blueprintDefinitionId");
            }

            if (createdFrom.HasValue)
            {
                clauses.Add("c.createdAt >= @createdFrom");
            }

            if (statuses is { Count: > 0 })
            {
                clauses.Add("ARRAY_CONTAINS(@statuses, c.status)");
            }

            var query = new QueryDefinition(
                    $"SELECT * FROM c WHERE {string.Join(" AND ", clauses)} ORDER BY c.createdAt DESC")
                .WithParameter("@partitionKey", partitionKey)
                .WithParameter("@createdTo", createdTo);

            if (blueprintDefinitionId.HasValue)
            {
                query = query.WithParameter("@blueprintDefinitionId", blueprintDefinitionId.Value);
            }

            if (createdFrom.HasValue)
            {
                query = query.WithParameter("@createdFrom", createdFrom.Value);
            }

            if (statuses is { Count: > 0 })
            {
                query = query.WithParameter("@statuses", statuses.Select(status => status.ToString()).ToArray());
            }

            using var iterator = _container.GetItemQueryIterator<ProcessSummaryProjectionRecord>(
                query,
                continuationToken,
                new QueryRequestOptions
                {
                    PartitionKey = new PartitionKey(partitionKey),
                    MaxItemCount = Math.Clamp(maxItemCount, 1, 250),
                });

            if (!iterator.HasMoreResults)
            {
                return new ProcessSummaryProjectionPartitionPage([], null);
            }

            var response = await iterator.ReadNextAsync(cancellationToken);
            return new ProcessSummaryProjectionPartitionPage(
                response.Resource.ToList(),
                response.ContinuationToken);
        }
    }
}
