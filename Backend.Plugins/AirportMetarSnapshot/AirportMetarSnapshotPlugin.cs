using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace AirportMetarSnapshot;

public sealed class AirportMetarSnapshotPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required AirportMetarInput Input { get; init; }
    public AirportMetarResult Metar { get; set; } = default!;

    [ActivatorUtilitiesConstructor]
    public AirportMetarSnapshotPlugin()
        : this(CreateHttpClient())
    {
    }

    public AirportMetarSnapshotPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task Execute()
    {
        var stationCode = Input.StationCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(stationCode))
        {
            throw new InvalidDataException("StationCode is required.");
        }

        var url = $"https://aviationweather.gov/api/data/metar?ids={stationCode}&format=json";
        var response = await _httpClient.GetFromJsonAsync<List<MetarObservation>>(url);
        var observation = response?.FirstOrDefault() ?? throw new InvalidDataException($"No METAR observation was returned for {stationCode}.");

        var ceiling = observation.Clouds?
            .Where(cloud => string.Equals(cloud.Cover, "BKN", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(cloud.Cover, "OVC", StringComparison.OrdinalIgnoreCase))
            .Select(cloud => cloud.Base)
            .DefaultIfEmpty(12000)
            .Min() ?? 12000;

        Metar = new AirportMetarResult
        {
            StationCode = stationCode,
            FlightCategory = observation.FlightCategory ?? "VFR",
            WindSpeedKnots = observation.WindSpeed ?? 0,
            VisibilityMiles = ParseVisibilityMiles(observation.Visibility),
            CeilingFeet = ceiling,
            Summary = observation.RawObservation ?? $"{stationCode} METAR {observation.FlightCategory ?? "VFR"}"
        };
    }

    private static double ParseVisibilityMiles(string? visibility)
    {
        if (string.IsNullOrWhiteSpace(visibility))
        {
            return 10;
        }

        var normalized = visibility.Replace("SM", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("+", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 10;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (airport-metar; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class MetarObservation
    {
        [JsonPropertyName("rawOb")]
        public string? RawObservation { get; set; }

        [JsonPropertyName("fltCat")]
        public string? FlightCategory { get; set; }

        [JsonPropertyName("wspd")]
        public double? WindSpeed { get; set; }

        [JsonPropertyName("visib")]
        public string? Visibility { get; set; }

        [JsonPropertyName("clouds")]
        public List<CloudLayer>? Clouds { get; set; }
    }

    private sealed class CloudLayer
    {
        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("base")]
        public int Base { get; set; }
    }
}
