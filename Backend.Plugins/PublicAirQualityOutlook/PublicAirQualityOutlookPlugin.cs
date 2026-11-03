using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace PublicAirQualityOutlook;

public sealed class PublicAirQualityOutlookPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required AirQualityWindowInput Input { get; init; }
    public AirQualityWindowResult AirQuality { get; set; } = default!;

    [ActivatorUtilitiesConstructor]
    public PublicAirQualityOutlookPlugin()
        : this(CreateHttpClient())
    {
    }

    public PublicAirQualityOutlookPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task Execute()
    {
        var locationLabel = Input.LocationLabel.Trim();
        var start = Input.WindowStartIsoDate;
        var end = Input.WindowEndIsoDate;

        if (string.IsNullOrWhiteSpace(locationLabel))
        {
            throw new InvalidDataException("LocationLabel is required.");
        }

        if (start == default || end == default)
        {
            throw new InvalidDataException("WindowStartIsoDate and WindowEndIsoDate are required.");
        }

        if (end < start)
        {
            throw new InvalidDataException("WindowEndIsoDate must be on or after WindowStartIsoDate.");
        }

        var url =
            $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={Input.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={Input.Longitude.ToString(CultureInfo.InvariantCulture)}&hourly=european_aqi&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}&timezone=UTC";

        var response = await _httpClient.GetFromJsonAsync<AirQualityResponse>(url);
        var values = response?.Hourly?.EuropeanAqi?
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        if (values is null || values.Count == 0)
        {
            throw new InvalidDataException("Air quality response did not contain usable european_aqi values.");
        }

        var averageAqi = values.Average();
        var score = (int)Math.Round(Math.Clamp(100.0 - averageAqi, 0, 100));

        AirQuality = new AirQualityWindowResult
        {
            LocationLabel = locationLabel,
            WindowStartIsoDate = start,
            WindowEndIsoDate = end,
            AverageAirQualityIndex = Math.Round(averageAqi, 1),
            AirQualityScore = score,
            Summary = $"Average European AQI across the execution window is {Math.Round(averageAqi, 1)}."
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (air-quality-window; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class AirQualityResponse
    {
        [JsonPropertyName("hourly")]
        public HourlyData? Hourly { get; set; }
    }

    private sealed class HourlyData
    {
        [JsonPropertyName("european_aqi")]
        public List<double?>? EuropeanAqi { get; set; }
    }
}
