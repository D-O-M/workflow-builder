using System.Text.Json;

namespace Backend.API.Observability;

public sealed class PrometheusQueryClient
{
    private readonly HttpClient _httpClient;

    public PrometheusQueryClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<JsonDocument> QueryAsync(string expression, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            $"/api/v1/query?query={Uri.EscapeDataString(expression)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);
    }
}
