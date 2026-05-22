using System.Net;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

public sealed class ProcessRealtimeProjectionRepository : IProcessRealtimeProjectionRepository
{
    private readonly SummaryRepository _summaries;
    private readonly NodeRepository _nodes;

    public ProcessRealtimeProjectionRepository(
        Container summariesContainer,
        Container nodesContainer)
    {
        _summaries = new SummaryRepository(summariesContainer);
        _nodes = new NodeRepository(nodesContainer);
    }

    public Task<ProcessRealtimeSummaryProjectionRecord?> GetSummaryAsync(
        Guid processId,
        CancellationToken cancellationToken) =>
        _summaries.GetAsync(
            processId.ToString(),
            new PartitionKey(processId.ToString()),
            cancellationToken);

    public async Task<ProcessRealtimeSummaryProjectionRecord> UpsertSummaryAsync(
        ProcessRealtimeSummaryProjectionRecord record,
        CancellationToken cancellationToken) =>
        (await _summaries.UpsertAsync(record, cancellationToken)).Resource;

    public Task<ProcessNodeRealtimeProjectionRecord?> GetNodeAsync(
        Guid processId,
        Guid blueprintNodeId,
        CancellationToken cancellationToken) =>
        _nodes.GetAsync(
            CreateNodeId(blueprintNodeId),
            new PartitionKey(processId.ToString()),
            cancellationToken);

    public async Task<IReadOnlyList<ProcessNodeRealtimeProjectionRecord>> GetNodesAsync(
        Guid processId,
        IReadOnlyCollection<Guid> blueprintNodeIds,
        CancellationToken cancellationToken)
    {
        if (blueprintNodeIds.Count == 0)
        {
            return [];
        }

        var records = new List<ProcessNodeRealtimeProjectionRecord>(blueprintNodeIds.Count);
        foreach (var blueprintNodeId in blueprintNodeIds.Distinct())
        {
            var record = await GetNodeAsync(processId, blueprintNodeId, cancellationToken);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    public async Task<(bool Created, ProcessNodeRealtimeProjectionRecord Record)> CreateNodeIfNotExistsAsync(
        ProcessNodeRealtimeProjectionRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, (await _nodes.CreateAsync(record, cancellationToken)).Resource);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetNodeAsync(
                Guid.Parse(record.ProcessId),
                Guid.Parse(record.BlueprintNodeId),
                cancellationToken);

            return (false, existing ?? record);
        }
    }

    public async Task<bool> TryUpdateNodeAsync(
        ProcessNodeRealtimeProjectionRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            await _nodes.ReplaceAsync(record, cancellationToken, record.ETag);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public static string CreateNodeId(Guid blueprintNodeId) =>
        blueprintNodeId.ToString();

    private sealed class SummaryRepository : CosmosRepository<ProcessRealtimeSummaryProjectionRecord>
    {
        public SummaryRepository(Container container)
            : base(container, record => record.Id, record => new PartitionKey(record.Id))
        {
        }
    }

    private sealed class NodeRepository : CosmosRepository<ProcessNodeRealtimeProjectionRecord>
    {
        public NodeRepository(Container container)
            : base(container, record => record.Id, record => new PartitionKey(record.ProcessId))
        {
        }
    }
}
