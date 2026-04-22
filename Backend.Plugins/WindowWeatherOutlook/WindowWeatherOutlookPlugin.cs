using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace WindowWeatherOutlook;

public sealed class WindowWeatherOutlookPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required WindowWeatherInput Input { get; init; }
    public WindowWeatherResult Weather { get; set; } = default!;

    public WindowWeatherOutlookPlugin()
        : this(CreateHttpClient())
    {
    }

    public WindowWeatherOutlookPlugin(HttpClient httpClient)
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
            $"https://api.open-meteo.com/v1/forecast?latitude={Input.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={Input.Longitude.ToString(CultureInfo.InvariantCulture)}&hourly=temperature_2m,precipitation,wind_speed_10m&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}&timezone=UTC";
        var response = await _httpClient.GetFromJsonAsync<WeatherResponse>(url);

        var temps = response?.Hourly?.Temperature2M ?? [];
        var precipitation = response?.Hourly?.Precipitation ?? [];
        var windsKmH = response?.Hourly?.WindSpeed10M ?? [];

        if (temps.Count == 0 || precipitation.Count == 0 || windsKmH.Count == 0)
        {
            throw new InvalidDataException("Weather response did not contain the required hourly series.");
        }

        var averageTemperature = temps.Average();
        var totalPrecipitation = precipitation.Sum();
        var maxWindMps = windsKmH.Max() / 3.6;

        var precipitationPenalty = Math.Clamp(totalPrecipitation * 9.0, 0, 45);
        var windPenalty = Math.Clamp(Math.Max(0, maxWindMps - 6.0) * 7.0, 0, 35);
        var temperaturePenalty = averageTemperature < 4
            ? Math.Clamp((4 - averageTemperature) * 5.0, 0, 20)
            : averageTemperature > 30
                ? Math.Clamp((averageTemperature - 30) * 4.0, 0, 20)
                : 0;

        var riskScore = (int)Math.Round(Math.Clamp(precipitationPenalty + windPenalty + temperaturePenalty, 0, 100));

        Weather = new WindowWeatherResult
        {
            LocationLabel = locationLabel,
            WindowStartIsoDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WindowEndIsoDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AverageTemperatureC = Math.Round(averageTemperature, 1),
            TotalPrecipitationMm = Math.Round(totalPrecipitation, 1),
            MaxWindSpeedMps = Math.Round(maxWindMps, 1),
            WeatherRiskScore = riskScore,
            Summary = $"Avg {Math.Round(averageTemperature, 1)}°C, precip {Math.Round(totalPrecipitation, 1)} mm, max wind {Math.Round(maxWindMps, 1)} m/s."
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (window-weather; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class WeatherResponse
    {
        [JsonPropertyName("hourly")]
        public HourlyWeather? Hourly { get; set; }
    }

    private sealed class HourlyWeather
    {
        [JsonPropertyName("temperature_2m")]
        public List<double> Temperature2M { get; set; } = [];

        [JsonPropertyName("precipitation")]
        public List<double> Precipitation { get; set; } = [];

        [JsonPropertyName("wind_speed_10m")]
        public List<double> WindSpeed10M { get; set; } = [];
    }
}
