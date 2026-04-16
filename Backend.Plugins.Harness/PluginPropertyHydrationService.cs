using Backend.Repositories;
using Microsoft.Azure.Cosmos;

namespace Backend.Plugins.Harness;

internal class PluginPropertyHydrationService<TDocument, TProperty>
    where TDocument : class
{
    private readonly CosmosRepository<TDocument> _cosmosRepository;
    private readonly Func<TDocument, string, TProperty> _documentValueExtractionFunction;

    public PluginPropertyHydrationService(
        CosmosRepository<TDocument> cosmosRepository,
        Func<TDocument, string, TProperty> documentValueExtractionFunction)
    {
        _cosmosRepository = cosmosRepository;
        _documentValueExtractionFunction = documentValueExtractionFunction;
    }

    public async Task HydratePluginProperty(
        Action<TProperty> propertySetter,
        string documentId,
        string partitionKey,
        string outputChannelName,
        CancellationToken cancellationToken)
    {
        var document = await GetDocument(
            documentId,
            partitionKey,
            cancellationToken);

        var value = _documentValueExtractionFunction(document, outputChannelName);

        propertySetter(value);
    }

    private async Task<TDocument> GetDocument(
        string id,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        var document = await _cosmosRepository.GetAsync(
            id,
            new PartitionKey(partitionKey),
            cancellationToken);

        if (document is null)
        {
            throw new EntityNotFoundException<TDocument>(id);
        }

        return document;
    }
}
