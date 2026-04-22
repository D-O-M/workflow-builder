using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace PublicAirQualityOutlook;

public sealed class PublicAirQualityOutlookPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required AirQualityWindowInput Input { get; init; }
    public AirQualityWindowResult AirQuality { get; set; } = default!;

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
        var start = ParseIsoDate(Input.WindowStartIsoDate, nameof(Input.WindowStartIsoDate));
        var end = ParseIsoDate(Input.WindowEndIsoDate, nameof(Input.WindowEndIsoDate));

        if (string.IsNullOrWhiteSpace(locationLabel))
        {
            throw new InvalidDataException("LocationLabel is required.");
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
            WindowStartIsoDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WindowEndIsoDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AverageAirQualityIndex = Math.Round(averageAqi, 1),
            AirQualityScore = score,
            Summary = $"Average European AQI across the execution window is {Math.Round(averageAqi, 1)}."
        };
    }

    private static DateOnly ParseIsoDate(string value, string propertyName)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new InvalidDataException($"{propertyName} must use yyyy-MM-dd format.");
        }

        return parsed;
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
