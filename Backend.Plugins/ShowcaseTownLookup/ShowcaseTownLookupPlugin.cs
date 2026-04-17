using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace ShowcaseTownLookup;

public sealed class ShowcaseTownLookupPlugin : IPlugin
{
    public required TownLookupRequest TownLookupRequest { get; init; }
    public ResolvedTown ResolvedTown { get; set; } = new();

    private static readonly HttpClient _http = CreateHttpClient();
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static DateTimeOffset _lastRequestUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan _minDelay = TimeSpan.FromSeconds(1);

    public async Task Execute()
    {
        var requestedTownName = TownLookupRequest.RequestedTownName?.Trim();
        if (string.IsNullOrWhiteSpace(requestedTownName))
            throw new InvalidDataException("RequestedTownName is required.");

        await _gate.WaitAsync();
        try
        {
            var wait = (_lastRequestUtc + _minDelay) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);

            var url =
                $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(requestedTownName)}&count=1&language=en&format=json";
            var response = await _http.GetFromJsonAsync<OpenMeteoSearchResponse>(url);
            _lastRequestUtc = DateTimeOffset.UtcNow;

            var best = response?.Results?.FirstOrDefault();
            if (best is null)
                throw new InvalidDataException($"Could not resolve town '{requestedTownName}'.");

            if (double.IsNaN(best.Latitude) || double.IsNaN(best.Longitude))
            {
                throw new InvalidDataException("The town lookup service returned invalid coordinates.");
            }

            var resolvedTownName = ResolveTownName(best, requestedTownName);
            var resolvedDisplayName = BuildDisplayName(best, resolvedTownName);
            var resolvedCountryCode = best.CountryCode?.Trim().ToUpperInvariant() ?? "";

            ResolvedTown = new ResolvedTown
            {
                RequestedTownName = requestedTownName,
                ResolvedTownName = resolvedTownName,
                ResolvedDisplayName = string.IsNullOrWhiteSpace(resolvedDisplayName)
                    ? resolvedTownName
                    : resolvedDisplayName,
                ResolvedCountryCode = resolvedCountryCode,
                Latitude = best.Latitude,
                Longitude = best.Longitude
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ResolveTownName(OpenMeteoResult result, string requestedTownName)
    {
        var resolvedTownName = FirstNonBlank(
            result.Name,
            result.Admin3,
            result.Admin2,
            result.Admin1,
            requestedTownName);

        return resolvedTownName ?? requestedTownName;
    }

    private static string? BuildDisplayName(OpenMeteoResult result, string resolvedTownName)
    {
        var parts = new[]
        {
            resolvedTownName,
            result.Admin1,
            result.Country
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        return parts.Length == 0 ? null : string.Join(", ", parts);
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "workflow-builder/1.0 (showcase-town-lookup; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class OpenMeteoSearchResponse
    {
        [JsonPropertyName("results")] public List<OpenMeteoResult> Results { get; set; } = [];
    }

    private sealed class OpenMeteoResult
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("latitude")] public double Latitude { get; set; }
        [JsonPropertyName("longitude")] public double Longitude { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
        [JsonPropertyName("admin1")] public string? Admin1 { get; set; }
        [JsonPropertyName("admin2")] public string? Admin2 { get; set; }
        [JsonPropertyName("admin3")] public string? Admin3 { get; set; }
    }
}
