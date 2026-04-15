using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace WeatherTownGeocode;

public sealed class TownGeocodePlugin : IPlugin
{
    public required TownInput Input { get; init; }
    public required GeoLocation Location { get; set; } = new();

    private static readonly HttpClient _http = CreateHttpClient();

    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private static readonly TimeSpan _minDelay = TimeSpan.FromSeconds(1);

    public async Task Execute()
    {
        if (string.IsNullOrWhiteSpace(Input.Town))
            throw new InvalidDataException("Town is required.");

        await _gate.WaitAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var wait = (_lastRequest + _minDelay) - now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);

            var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(Input.Town)}&format=json&limit=1";
            var results = await _http.GetFromJsonAsync<List<NominatimResult>>(url);
            _lastRequest = DateTimeOffset.UtcNow;

            var best = results?.FirstOrDefault();
            if (best is null)
                throw new InvalidDataException($"Could not geocode town '{Input.Town}'.");

            if (!double.TryParse(best.Lat, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(best.Lon, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon))
            {
                throw new InvalidDataException("Invalid lat/lon returned by Nominatim.");
            }

            Location.Town = Input.Town;
            Location.Latitude = lat;
            Location.Longitude = lon;
            Location.DisplayName = best.DisplayName;
            Location.CountryCode = best.Address?.CountryCode;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "workflow-builder/1.0 (geocoding; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")] public string Lat { get; set; } = "";
        [JsonPropertyName("lon")] public string Lon { get; set; } = "";
        [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
        [JsonPropertyName("address")] public NominatimAddress? Address { get; set; }
    }

    private sealed class NominatimAddress
    {
        [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
    }
}
