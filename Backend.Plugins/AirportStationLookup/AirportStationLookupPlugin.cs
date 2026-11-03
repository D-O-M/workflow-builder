using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace AirportStationLookup;

public sealed class AirportStationLookupPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required AirportStationLookupInput Input { get; init; }
    public AirportStationLookupResult Station { get; set; } = default!;

    [ActivatorUtilitiesConstructor]
    public AirportStationLookupPlugin()
        : this(CreateHttpClient())
    {
    }

    public AirportStationLookupPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task Execute()
    {
        var airportCode = Input.AirportCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(airportCode))
        {
            throw new InvalidDataException("AirportCode is required.");
        }

        var url = $"https://aviationweather.gov/api/data/stationinfo?ids={airportCode}&format=json";
        var response = await _httpClient.GetFromJsonAsync<List<StationInfo>>(url);
        var station = response?.FirstOrDefault() ?? throw new InvalidDataException($"No station information was returned for {airportCode}.");

        Station = new AirportStationLookupResult
        {
            AirportCode = airportCode,
            StationCode = station.IcaoId ?? airportCode,
            AirportName = station.Site ?? station.IcaoId ?? airportCode,
            Latitude = station.Latitude,
            Longitude = station.Longitude,
            CountryCode = station.Country ?? string.Empty
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (airport-station; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class StationInfo
    {
        [JsonPropertyName("icaoId")]
        public string? IcaoId { get; set; }

        [JsonPropertyName("site")]
        public string? Site { get; set; }

        [JsonPropertyName("lat")]
        public double Latitude { get; set; }

        [JsonPropertyName("lon")]
        public double Longitude { get; set; }

        [JsonPropertyName("country")]
        public string? Country { get; set; }
    }
}
