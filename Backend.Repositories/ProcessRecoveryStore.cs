using System.Net;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Database.State.Objects;
using Microsoft.Azure.Cosmos;

namespace Backend.Repositories;

// A revision is staged under a fresh stamp. Only the header publishes that stamp,
// so a crashed or competing preview writer can never publish a partial cohort.
public sealed record RecoveryBatch
{
    public string Id { get; init; } = "batch";
    public string Kind => "batch";
    public required Guid BatchId { get; init; }
    public required Guid Stamp { get; init; }
    public int Revision { get; init; } = 1;
    public string NewWorkPolicy { get; init; } = "Pause";
    public string Status { get; init; } = "Preview";
    public required DateTimeOffset CreatedAt { get; init; }
    public required string Actor { get; init; }
    public DateTimeOffset? ConfirmedAt { get; init; }
    public List<int> ConfirmedRevisions { get; init; } = [];
    public string? Reason { get; init; }
    public int ProcessCount { get; init; }
    public int EligibleProcessCount { get; init; }
    public int StepCount { get; init; }
    public long NextPoll { get; init; }
    [JsonPropertyName("_etag")] public string? ETag { get; init; }
}

public sealed record RecoveryCase
{
    public required string Id { get; init; }
    public string Kind => "case";
    public required Guid BatchId { get; init; }
    public required Guid Stamp { get; init; }
    public required Guid ProcessId { get; init; }
    public required int BlueprintDefinitionId { get; init; }
    public required int BlueprintVersionId { get; init; }
    public required int Ordinal { get; init; }
    [JsonIgnore] public RecoveryCasePlan Plan { get; init; } = new();
    public int PlanPartCount { get; init; }
    public string Status { get; init; } = "Preview";
    public string? Message { get; init; }
    public long NextPoll { get; init; }
    [JsonPropertyName("_etag")] public string? ETag { get; init; }
}

public sealed class RecoveryConflictException(string message) : Exception(message);

public interface IProcessRecoveryStore
{
    Task<RecoveryBatch?> ReadAsync(Guid id, CancellationToken ct);
    Task<RecoveryBatch?> ReadConfirmationAsync(Guid id, int revision, CancellationToken ct);
    Task StageAsync(RecoveryCase item, CancellationToken ct);
    Task CreateAsync(RecoveryBatch batch, CancellationToken ct);
    Task<bool> ReplaceAsync(RecoveryBatch batch, CancellationToken ct);
    Task<RecoveryBatch> ConfirmAsync(RecoveryBatch batch, CancellationToken ct);
    Task<List<RecoveryCase>> PageAsync(RecoveryBatch batch, int offset, int limit, CancellationToken ct);
    Task<List<RecoveryBatch>> RecentAsync(int offset, int limit, CancellationToken ct);
    IAsyncEnumerable<Guid> FindForProcessAsync(Guid processId, CancellationToken ct);
}

public sealed class ProcessRecoveryStore(Container container) : IProcessRecoveryStore
{
    public static string CaseId(Guid stamp, Guid processId) => $"{stamp:N}-{processId:N}";

    public async Task<RecoveryBatch?> ReadAsync(Guid id, CancellationToken ct)
    {
        try
        {
            return (await container.ReadItemAsync<RecoveryBatch>("batch", Key(id), cancellationToken: ct)).Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // A plan contains snapshots of inputs and outputs from several Step documents.
    // Keep it immutable and bounded per Cosmos item, independently of lease/status writes.
    private const int PartSize = 512 * 1024;
    private static readonly JsonSerializerOptions PlanJson = CosmosSerialization.CreateJsonOptions();
    private sealed record PlanPart(string Id, Guid BatchId, byte[] Data);

    public async Task StageAsync(RecoveryCase item, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        await using (var compressed = new BrotliStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
            await JsonSerializer.SerializeAsync(compressed, item.Plan, PlanJson, ct);
        var payload = bytes.GetBuffer();
        var length = checked((int)bytes.Length);
        var parts = 0;
        for (var offset = 0; offset < length; offset += PartSize)
        {
            var part = new PlanPart($"{item.Id}-plan-{parts++}", item.BatchId,
                payload.AsSpan(offset, Math.Min(PartSize, length - offset)).ToArray());
            await container.CreateItemAsync(part, Key(item.BatchId), cancellationToken: ct);
        }
        await container.CreateItemAsync(item with { PlanPartCount = parts }, Key(item.BatchId), cancellationToken: ct);
    }

    private async Task<RecoveryCase> LoadPlanAsync(RecoveryCase item, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        for (int part = 0; part < item.PlanPartCount; part++)
        {
            var response = await container.ReadItemAsync<PlanPart>($"{item.Id}-plan-{part}", Key(item.BatchId), cancellationToken: ct);
            stream.Write(response.Resource.Data);
        }
        stream.Position = 0;
        using var decompressor = new BrotliStream(stream, CompressionMode.Decompress);
        var plan = await JsonSerializer.DeserializeAsync<RecoveryCasePlan>(decompressor, PlanJson, ct)
            ?? throw new InvalidDataException("Missing recovery plan.");
        return item with { Plan = plan };
    }

    private sealed record Confirmation(string Id, Guid BatchId, RecoveryBatch Snapshot)
    {
        public string Kind => "confirmation";
    }

    public async Task<RecoveryBatch?> ReadConfirmationAsync(Guid id, int revision, CancellationToken ct)
    {
        try
        {
            var result = await container.ReadItemAsync<Confirmation>($"confirmation-{revision}", Key(id), cancellationToken: ct);
            return result.Resource.Snapshot with { Status = "Completed" };
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task CreateAsync(RecoveryBatch batch, CancellationToken ct) =>
        await container.CreateItemAsync(batch, Key(batch.BatchId), cancellationToken: ct);

    public Task<bool> ReplaceAsync(RecoveryBatch batch, CancellationToken ct) =>
        ReplaceAsync(batch, batch.Id, batch.BatchId, batch.ETag!, ct);

    public Task<bool> ReplaceAsync(RecoveryCase item, CancellationToken ct) =>
        ReplaceAsync(item, item.Id, item.BatchId, item.ETag!, ct);

    public async Task<RecoveryCase?> ClaimAsync(RecoveryCase item, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var claimed = item with { Status = "Running", NextPoll = now.AddMinutes(1).ToUnixTimeMilliseconds() };
            var result = await container.ReplaceItemAsync(claimed, item.Id, Key(item.BatchId),
                new ItemRequestOptions { IfMatchEtag = item.ETag }, ct);
            return await LoadPlanAsync(claimed with { ETag = result.ETag }, ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public async Task<RecoveryBatch> ConfirmAsync(RecoveryBatch batch, CancellationToken ct)
    {
        // Preserve actor and reason per confirmed revision even after a held revision is resumed.
        var response = await container.CreateTransactionalBatch(Key(batch.BatchId))
            .ReplaceItem("batch", batch, new TransactionalBatchItemRequestOptions { IfMatchEtag = batch.ETag })
            .CreateItem(new Confirmation($"confirmation-{batch.Revision}", batch.BatchId, batch))
            .ExecuteAsync(ct);
        using (response)
        {
            if (response.IsSuccessStatusCode) return batch;
            if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed))
                throw new CosmosException(response.ErrorMessage, response.StatusCode, 0, response.ActivityId, response.RequestCharge);
        }
        var current = await ReadAsync(batch.BatchId, ct);
        if (current?.Revision == batch.Revision && current.ConfirmedAt.HasValue) return current;
        throw new RecoveryConflictException("The recovery preview has changed. Review the current revision.");
    }

    public async Task<List<RecoveryCase>> PageAsync(RecoveryBatch batch, int offset, int limit, CancellationToken ct)
    {
        var items = await ReadPageAsync<RecoveryCase>(new QueryDefinition(
            "SELECT * FROM c WHERE c.kind='case' AND c.stamp=@stamp ORDER BY c.ordinal OFFSET @offset LIMIT @limit")
            .WithParameter("@stamp", batch.Stamp.ToString()).WithParameter("@offset", offset)
            .WithParameter("@limit", limit), Key(batch.BatchId), ct);
        var result = new List<RecoveryCase>(items.Count);
        foreach (var item in items) result.Add(await LoadPlanAsync(item, ct));
        return result;
    }

    public Task<List<RecoveryBatch>> DueBatchesAsync(long now, CancellationToken ct) =>
        ReadPageAsync<RecoveryBatch>(new QueryDefinition(
            "SELECT TOP 8 * FROM c WHERE c.kind='batch' AND c.status='Confirmed' AND c.nextPoll<=@now ORDER BY c.nextPoll")
            .WithParameter("@now", now), null, ct);

    public Task<List<RecoveryBatch>> RecentAsync(int offset, int limit, CancellationToken ct) =>
        ReadPageAsync<RecoveryBatch>(new QueryDefinition(
            "SELECT * FROM c WHERE c.kind='batch' ORDER BY c.createdAt DESC OFFSET @offset LIMIT @limit")
            .WithParameter("@offset", offset).WithParameter("@limit", limit), null, ct);

    public Task<List<RecoveryCase>> DueCasesAsync(RecoveryBatch batch, long now, CancellationToken ct) =>
        ReadPageAsync<RecoveryCase>(new QueryDefinition(
            "SELECT TOP 16 * FROM c WHERE c.kind='case' AND c.stamp=@stamp AND c.status IN ('Preview','Running') AND c.nextPoll<=@now ORDER BY c.nextPoll")
            .WithParameter("@stamp", batch.Stamp.ToString()).WithParameter("@now", now), Key(batch.BatchId), ct);

    public async Task<bool> HasUnfinishedCasesAsync(RecoveryBatch batch, CancellationToken ct) =>
        (await ReadPageAsync<int>(new QueryDefinition(
            "SELECT TOP 1 VALUE 1 FROM c WHERE c.kind='case' AND c.stamp=@stamp AND c.status IN ('Preview','Running')")
            .WithParameter("@stamp", batch.Stamp.ToString()), Key(batch.BatchId), ct)).Count != 0;

    public async IAsyncEnumerable<Guid> FindForProcessAsync(Guid processId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var query = new QueryDefinition("SELECT DISTINCT VALUE c.batchId FROM c WHERE c.kind='case' AND c.processId=@processId ORDER BY c.batchId")
            .WithParameter("@processId", processId.ToString());
        using var iterator = container.GetItemQueryIterator<Guid>(query);
        while (iterator.HasMoreResults)
            foreach (var id in await iterator.ReadNextAsync(ct)) yield return id;
    }

    private async Task<List<T>> ReadPageAsync<T>(QueryDefinition query, PartitionKey? key, CancellationToken ct)
    {
        var items = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = key });
        while (iterator.HasMoreResults) items.AddRange(await iterator.ReadNextAsync(ct));
        return items;
    }

    private async Task<bool> ReplaceAsync<T>(T value, string id, Guid batchId, string etag, CancellationToken ct)
    {
        try
        {
            await container.ReplaceItemAsync(value, id, Key(batchId),
                new ItemRequestOptions { IfMatchEtag = etag }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    private static PartitionKey Key(Guid id) => new(id.ToString());
}
