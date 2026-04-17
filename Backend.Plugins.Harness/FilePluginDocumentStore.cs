using System.Text.Json;

namespace Backend.Plugins.Harness;

internal sealed class FilePluginDocumentStore<TDocument> : IPluginDocumentStore<TDocument>
    where TDocument : class
{
    private readonly string _rootPath;
    private readonly string _scopeName;

    public FilePluginDocumentStore(string rootPath, string scopeName)
    {
        _rootPath = rootPath;
        _scopeName = scopeName;
    }

    public async Task<TDocument?> GetAsync(
        string documentId,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_rootPath, _scopeName, partitionKey, $"{documentId}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TDocument>(
            stream,
            JsonSerializerOptions.Web,
            cancellationToken);
    }
}
