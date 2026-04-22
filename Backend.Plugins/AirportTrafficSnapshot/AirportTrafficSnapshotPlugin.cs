using System.Globalization;
using System.Text.Json;
using Backend.Plugins.Harness;

namespace AirportTrafficSnapshot;

public sealed class AirportTrafficSnapshotPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required AirportTrafficInput Input { get; init; }
    public AirportTrafficResult Traffic { get; set; } = default!;

    public AirportTrafficSnapshotPlugin()
        : this(CreateHttpClient())
    {
    }

    public AirportTrafficSnapshotPlugin(HttpClient httpClient)
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

        var url =
            $"https://opensky-network.org/api/states/all?lamin={(Input.Latitude - 1.5).ToString(CultureInfo.InvariantCulture)}&lomin={(Input.Longitude - 1.5).ToString(CultureInfo.InvariantCulture)}&lamax={(Input.Latitude + 1.5).ToString(CultureInfo.InvariantCulture)}&lomax={(Input.Longitude + 1.5).ToString(CultureInfo.InvariantCulture)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("workflow-builder/1.0 (airport-traffic; contact=admin@example.invalid)");
        var token = Environment.GetEnvironmentVariable("OPENSKY_API_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);

        var states = document.RootElement.TryGetProperty("states", out var statesElement) && statesElement.ValueKind == JsonValueKind.Array
            ? statesElement.EnumerateArray().ToList()
            : [];

        var nearbyCount = states.Count;
        var groundCount = states.Count(state =>
            state.ValueKind == JsonValueKind.Array &&
            state.GetArrayLength() > 8 &&
            state[8].ValueKind == JsonValueKind.True);

        var pressureScore = (int)Math.Round(Math.Clamp((nearbyCount * 2.5) + (groundCount * 3.0), 0, 100));

        Traffic = new AirportTrafficResult
        {
            AirportCode = airportCode,
            NearbyAircraftCount = nearbyCount,
            GroundAircraftCount = groundCount,
            TrafficPressureScore = pressureScore,
            Summary = $"OpenSky snapshot shows {nearbyCount} aircraft in the local airspace, with {groundCount} on the ground."
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }
}
