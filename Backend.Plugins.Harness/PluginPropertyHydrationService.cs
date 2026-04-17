using Backend.Interfaces.Repositories;
using Backend.Repositories;

namespace Backend.Plugins.Harness;

internal sealed class PluginPropertyHydrationService<TDocument, TProperty>
    where TDocument : class
{
    private readonly IPluginDocumentStore<TDocument> _documentStore;
    private readonly Func<TDocument, string, TProperty> _documentValueExtractionFunction;

    public PluginPropertyHydrationService(
        IPluginDocumentStore<TDocument> documentStore,
        Func<TDocument, string, TProperty> documentValueExtractionFunction)
    {
        _documentStore = documentStore;
        _documentValueExtractionFunction = documentValueExtractionFunction;
    }

    public async Task HydratePluginProperty(
        Action<TProperty> propertySetter,
        string documentId,
        string partitionKey,
        string outputChannelName,
        CancellationToken cancellationToken)
    {
        var document = await _documentStore.GetAsync(documentId, partitionKey, cancellationToken);

        if (document is null)
        {
            throw new EntityNotFoundException<TDocument>(documentId);
        }

        var value = _documentValueExtractionFunction(document, outputChannelName);
        propertySetter(value);
    }
}
