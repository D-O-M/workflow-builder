using Backend.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Plugins.Harness;

internal sealed class CosmosPluginDocumentStore<TDocument> : IPluginDocumentStore<TDocument>
    where TDocument : class
{
    private readonly CosmosRepository<TDocument> _repository;

    public CosmosPluginDocumentStore(CosmosRepository<TDocument> repository)
    {
        _repository = repository;
    }

    public Task<TDocument?> GetAsync(
        string documentId,
        string partitionKey,
        CancellationToken cancellationToken) =>
        _repository.GetAsync(
            documentId,
            new PartitionKey(partitionKey),
            cancellationToken);
}
