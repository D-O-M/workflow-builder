namespace Backend.Plugins.Harness;

internal interface IPluginDocumentStore<TDocument>
    where TDocument : class
{
    Task<TDocument?> GetAsync(
        string documentId,
        string partitionKey,
        CancellationToken cancellationToken);
}
