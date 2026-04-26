using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Backend.Plugins.Harness;

internal sealed class CosmosPluginDocumentStore<TDocument> : IPluginDocumentStore<TDocument>
    where TDocument : class
{
    private const string CosmosApiVersion = "2018-12-31";
    private readonly HttpClient _httpClient;
    private readonly string _databaseName;
    private readonly string _containerName;
    private readonly string _accountKey;

    public CosmosPluginDocumentStore(
        HttpClient httpClient,
        string databaseName,
        string containerName,
        string accountKey)
    {
        _httpClient = httpClient;
        _databaseName = databaseName;
        _containerName = containerName;
        _accountKey = accountKey;
    }

    public async Task<TDocument?> GetAsync(
        string documentId,
        string partitionKey,
        CancellationToken cancellationToken)
    {
        var resourceLink = $"dbs/{_databaseName}/colls/{_containerName}/docs/{documentId}";
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            resourceLink);

        var date = DateTime.UtcNow.ToString("r").ToLowerInvariant();
        request.Headers.TryAddWithoutValidation("x-ms-date", date);
        request.Headers.TryAddWithoutValidation("x-ms-version", CosmosApiVersion);
        request.Headers.TryAddWithoutValidation(
            "x-ms-documentdb-partitionkey",
            JsonSerializer.Serialize(new[] { partitionKey }));
        request.Headers.TryAddWithoutValidation(
            "authorization",
            BuildAuthorizationToken(
                "get",
                "docs",
                resourceLink,
                date,
                _accountKey));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return await JsonSerializer.DeserializeAsync<TDocument>(
                contentStream,
                PluginCosmosSerialization.CreateJsonOptions(),
                cancellationToken);
        }

        using var reader = new StreamReader(contentStream);
        var errorBody = await reader.ReadToEndAsync(cancellationToken);
        throw new HttpRequestException(
            $"Cosmos point read failed with {(int)response.StatusCode} ({response.StatusCode}). {errorBody}",
            inner: null,
            response.StatusCode);
    }

    private static string BuildAuthorizationToken(
        string verb,
        string resourceType,
        string resourceLink,
        string date,
        string accountKey)
    {
        var payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceLink}\n{date.ToLowerInvariant()}\n\n";
        var key = Convert.FromBase64String(accountKey);

        using var hmac = new HMACSHA256(key);
        var signature = Convert.ToBase64String(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));
        return Uri.EscapeDataString($"type=master&ver=1.0&sig={signature}");
    }
}
