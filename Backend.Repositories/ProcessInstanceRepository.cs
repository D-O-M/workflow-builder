using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;
using System.Net;

namespace Backend.Repositories;

public class ProcessInstanceRepository<TGlobalState>
    : CosmosRepository<ProcessInstanceRecord<TGlobalState>>,
    IProcessInstanceRepository<TGlobalState>
{
    public ProcessInstanceRepository(Container container)
        : base(container, wo => wo.Id, wo => new PartitionKey(wo.BlueprintId))
    {}

    public async Task<ProcessInstanceRecord<TGlobalState>> CreateProcessState(
        ProcessInstanceRecord<TGlobalState> state,
        CancellationToken cancellationToken)
    {
        return (await CreateAsync(state, cancellationToken)).Resource;
    }

    public async Task<ProcessInstanceRecord<TGlobalState>> GetProcessStateById(
        Guid processId,
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var processState = await GetAsync(
            processId.ToString(),
            new PartitionKey(blueprintVersionId.ToString()),
            cancellationToken);

        if (processState is null)
        {
            throw new EntityNotFoundException<ProcessInstanceRecord<TGlobalState>>(
                processId.ToString());
        }

        return processState;
    }

    public async Task<List<ProcessInstanceRecord<TGlobalState>>> GetProcessStatesForBlueprint(
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.blueprintId = @blueprintId ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", blueprintVersionId.ToString());

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(blueprintVersionId.ToString())
            });
    }

    public async Task<ProcessStateSummary?> GetLatestProcessStateForBlueprint(
        int blueprintVersionId,
        CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 c.id, c.blueprintId, c.createdAt, c.updatedAt, c.status " +
            "FROM c WHERE c.blueprintId = @blueprintId ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", blueprintVersionId.ToString());

        using var iterator = Container.GetItemQueryIterator<ProcessStateSummary>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(blueprintVersionId.ToString()),
                MaxItemCount = 1,
            });

        if (!iterator.HasMoreResults)
        {
            return null;
        }

        var response = await iterator.ReadNextAsync(cancellationToken);
        return response.Resource.FirstOrDefault();
    }

    public async Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
        int blueprintVersionId,
        string? continuationToken,
        int maxItemCount,
        CancellationToken cancellationToken) =>
        await GetProcessStateSummaryPageForBlueprint(
            blueprintVersionId,
            continuationToken,
            maxItemCount,
            createdFrom: null,
            createdTo: null,
            statuses: null,
            cancellationToken);

    public async Task<ProcessStateSummaryPage> GetProcessStateSummaryPageForBlueprint(
        int blueprintVersionId,
        string? continuationToken,
        int maxItemCount,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        IReadOnlyCollection<ProcessStatus>? statuses,
        CancellationToken cancellationToken)
    {
        var clauses = new List<string>
        {
            "c.blueprintId = @blueprintId"
        };

        var query = new QueryDefinition(
            "SELECT c.id, c.blueprintId, c.createdAt, c.updatedAt, c.status " +
            "FROM c WHERE c.blueprintId = @blueprintId");

        query = query.WithParameter("@blueprintId", blueprintVersionId.ToString());

        if (createdFrom.HasValue)
        {
            clauses.Add("c.createdAt >= @createdFrom");
            query = query.WithParameter("@createdFrom", createdFrom.Value);
        }

        if (createdTo.HasValue)
        {
            clauses.Add("c.createdAt <= @createdTo");
            query = query.WithParameter("@createdTo", createdTo.Value);
        }

        if (statuses is { Count: > 0 })
        {
            clauses.Add("ARRAY_CONTAINS(@statuses, c.status)");
            query = query.WithParameter("@statuses", statuses.Select(status => status.ToString()).ToArray());
        }

        query = new QueryDefinition(
                "SELECT c.id, c.blueprintId, c.createdAt, c.updatedAt, c.status " +
                $"FROM c WHERE {string.Join(" AND ", clauses)} ORDER BY c.createdAt DESC")
            .WithParameter("@blueprintId", blueprintVersionId.ToString());

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

        using var iterator = Container.GetItemQueryIterator<ProcessStateSummary>(
            query,
            continuationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(blueprintVersionId.ToString()),
                MaxItemCount = maxItemCount,
            });

        if (!iterator.HasMoreResults)
        {
            return new ProcessStateSummaryPage
            {
                Items = [],
                ContinuationToken = null,
            };
        }

        var response = await iterator.ReadNextAsync(cancellationToken);
        return new ProcessStateSummaryPage
        {
            Items = response.Resource.ToList(),
            ContinuationToken = response.ContinuationToken,
        };
    }

    public async Task<IReadOnlyList<ProcessInstanceRecord<TGlobalState>>> GetRecentProcessStatesForReconciliationAsync(
        DateTimeOffset createdFrom,
        int maxItemCount,
        IReadOnlyCollection<ProcessStatus> statuses,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxItemCount, 1, 500);
        var normalizedStatuses = statuses.Count > 0
            ? statuses.Select(status => status.ToString()).ToArray()
            : [ProcessStatus.Pending.ToString(), ProcessStatus.InProgress.ToString()];
        var query = new QueryDefinition($"""
            SELECT TOP {limit} *
            FROM c
            WHERE c.createdAt >= @createdFrom
              AND ARRAY_CONTAINS(@statuses, c.status)
            ORDER BY c.createdAt DESC
            """)
            .WithParameter("@createdFrom", createdFrom)
            .WithParameter("@statuses", normalizedStatuses);

        return await QueryToListAsync(
            query,
            cancellationToken,
            new QueryRequestOptions
            {
                MaxItemCount = limit
            });
    }

    public async Task<ProcessInstanceRecord<TGlobalState>> UpdateProcessState(
        ProcessInstanceRecord<TGlobalState> state,
        CancellationToken cancellationToken)
    {
        return (await ReplaceAsync(state, cancellationToken)).Resource;
    }

    public async Task<bool> TryUpdateProcessState(
        ProcessInstanceRecord<TGlobalState> state,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReplaceAsync(state, cancellationToken, state.ETag);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}
