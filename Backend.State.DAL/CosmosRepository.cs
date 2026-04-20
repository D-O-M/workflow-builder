using System.Runtime.CompilerServices;
using Microsoft.Azure.Cosmos;

#nullable enable

/// <summary>
/// A small, reusable Cosmos DB CRUD wrapper for a single container.
/// Assumptions:
/// - Your items have an "id" property (Cosmos requirement) surfaced as C# "Id".
/// - You can provide the partition key value for each item via a selector.
/// </summary>
public class CosmosRepository<T> where T : class
{
    private readonly Func<T, string> _idSelector;
    private readonly Func<T, PartitionKey> _partitionKeySelector;
    protected Container Container { get; }

    public CosmosRepository(
        Container container,
        Func<T, string> idSelector,
        Func<T, PartitionKey> partitionKeySelector)
    {
        Container = container ?? throw new ArgumentNullException(nameof(container));
        _idSelector = idSelector ?? throw new ArgumentNullException(nameof(idSelector));
        _partitionKeySelector = partitionKeySelector ?? throw new ArgumentNullException(nameof(partitionKeySelector));
    }

    /// <summary>Create a new item. Fails if the id already exists within the partition.</summary>
    public async Task<ItemResponse<T>> CreateAsync(
        T item,
        CancellationToken cancellationToken,
        ItemRequestOptions? options = null)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        return await Container.CreateItemAsync(item, _partitionKeySelector(item), options, cancellationToken)
                               .ConfigureAwait(false);
    }

    /// <summary>
    /// Upsert item (create if missing, replace if exists).
    /// Note: Upsert is convenient but can hide accidental overwrites; use ReplaceAsync with ETag for concurrency.
    /// </summary>
    public async Task<ItemResponse<T>> UpsertAsync(
        T item,
        CancellationToken cancellationToken,
        ItemRequestOptions? options = null)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));
        return await Container.UpsertItemAsync(item, _partitionKeySelector(item), options, cancellationToken)
                               .ConfigureAwait(false);
    }

    /// <summary>
    /// Point read by id + partition key.
    /// Returns null if not found.
    /// </summary>
    public async Task<T?> GetAsync(
        string id,
        PartitionKey partitionKey,
        CancellationToken cancellationToken,
        ItemRequestOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required.", nameof(id));

        try
        {
            var resp = await Container.ReadItemAsync<T>(
                id, partitionKey, options, cancellationToken);
            return resp.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Replace an existing item. Optionally provide ETag for optimistic concurrency (If-Match).
    /// </summary>
    public async Task<ItemResponse<T>> ReplaceAsync(
        T item,
        CancellationToken cancellationToken,
        string? ifMatchEtag = null,
        ItemRequestOptions? options = null)
    {
        if (item is null) throw new ArgumentNullException(nameof(item));

        options ??= new ItemRequestOptions();
        if (!string.IsNullOrWhiteSpace(ifMatchEtag))
            options.IfMatchEtag = ifMatchEtag;

        return await Container.ReplaceItemAsync(
                item: item,
                id: _idSelector(item),
                partitionKey: _partitionKeySelector(item),
                requestOptions: options,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Delete by id + partition key.</summary>
    public async Task<ItemResponse<T>> DeleteAsync(
        string id,
        PartitionKey partitionKey,
        CancellationToken cancellationToken,
        ItemRequestOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id is required.", nameof(id));

        return await Container.DeleteItemAsync<T>(id, partitionKey, options, cancellationToken)
                               .ConfigureAwait(false);
    }

    /// <summary>
    /// Run a SQL query against the container and stream results.
    /// </summary>
    public async IAsyncEnumerable<T> QueryAsync(
        QueryDefinition query,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        QueryRequestOptions? options = null)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));

        using var feed = Container.GetItemQueryIterator<T>(query, requestOptions: options);

        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in page)
                yield return item;
        }
    }

    /// <summary>
    /// Convenience overload for simple string SQL queries.
    /// </summary>
    public IAsyncEnumerable<T> QueryAsync(
        string sql,
        CancellationToken cancellationToken,
        QueryRequestOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("SQL is required.", nameof(sql));
        return QueryAsync(new QueryDefinition(sql), cancellationToken, options);
    }

    /// <summary>
    /// Execute a query and materialize into a list (use sparingly on large result sets).
    /// </summary>
    public async Task<List<T>> QueryToListAsync(
        QueryDefinition query,
        CancellationToken cancellationToken,
        QueryRequestOptions? options = null)
    {
        var results = new List<T>();
        await foreach (var item in QueryAsync(query, cancellationToken, options).ConfigureAwait(false))
            results.Add(item);
        return results;
    }

    /// <summary>
    /// Bulk transactional batch within a single logical partition key.
    /// </summary>
    public TransactionalBatch CreateBatch(PartitionKey partitionKey) =>
        Container.CreateTransactionalBatch(partitionKey);
}
