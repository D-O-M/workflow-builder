using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace AirportAdvisorySnapshot;

public sealed class AirportAdvisorySnapshotPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required AirportAdvisoryInput Input { get; init; }
    public AirportAdvisoryResult Advisory { get; set; } = default!;

    public AirportAdvisorySnapshotPlugin()
        : this(CreateHttpClient())
    {
    }

    public AirportAdvisorySnapshotPlugin(HttpClient httpClient)
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

        var advisories = await _httpClient.GetFromJsonAsync<List<AirSigmet>>("https://aviationweather.gov/api/data/airsigmet?format=json") ?? [];
        var relevant = advisories
            .Where(advisory => advisory.Coordinates.Any(point =>
                Math.Abs(point.Latitude - Input.Latitude) <= 4.0 &&
                Math.Abs(point.Longitude - Input.Longitude) <= 4.0))
            .ToList();

        var count = relevant.Count;
        var maxSeverity = relevant.Count == 0 ? 0 : relevant.Max(advisory => advisory.Severity ?? 0);

        Advisory = new AirportAdvisoryResult
        {
            AirportCode = airportCode,
            AdvisoryCount = count,
            HighestAdvisorySeverityScore = maxSeverity,
            Summary = count == 0
                ? "No nearby active SIGMET/G-AIRMET advisories were found."
                : $"{count} nearby advisory product(s); highest severity score {maxSeverity}."
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (airport-advisory; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class AirSigmet
    {
        [JsonPropertyName("severity")]
        public int? Severity { get; set; }

        [JsonPropertyName("coords")]
        public List<Coordinate> Coordinates { get; set; } = [];
    }

    private sealed class Coordinate
    {
        [JsonPropertyName("lat")]
        public double Latitude { get; set; }

        [JsonPropertyName("lon")]
        public double Longitude { get; set; }
    }
}
