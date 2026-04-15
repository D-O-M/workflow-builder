using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace WeatherWeekendForecast;

public sealed class WeekendForecastPlugin : IPlugin
{
    public required GeoLocation Location { get; init; }
    public WeekendForecastScore? Forecast { get; set; }

    private static readonly HttpClient _http = CreateHttpClient();

    public async Task Execute()
    {
        var weekend = GetNextWeekendUtc();

        var url = $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={Location.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lon={Location.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var model = await _http.GetFromJsonAsync<LocationForecastResponse>(url);
        if (model?.Properties?.Timeseries is null || model.Properties.Timeseries.Count == 0)
            throw new InvalidDataException("MET Norway response missing timeseries.");

        var start = weekend.StartUtc;
        var end = weekend.EndUtc;

        var window = model.Properties.Timeseries
            .Where(t => t.Time >= start && t.Time <= end)
            .ToList();

        if (window.Count == 0)
            throw new InvalidDataException("No forecast points for the selected weekend window.");

        var temps = window.Select(t => t.Data?.Instant?.Details?.AirTemperature).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var winds = window.Select(t => t.Data?.Instant?.Details?.WindSpeed).Where(v => v.HasValue).Select(v => v!.Value).ToList();

        var precip = window.Select(t =>
                t.Data?.Next1Hours?.Details?.PrecipitationAmount
                ?? t.Data?.Next6Hours?.Details?.PrecipitationAmount
                ?? t.Data?.Next12Hours?.Details?.PrecipitationAmount)
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();

        var totalPrecip = precip.Sum();
        var avgTemp = temps.Count > 0 ? temps.Average() : 0;
        var maxWind = winds.Count > 0 ? winds.Max() : 0;

        var score = 100;

        score -= (int)Math.Clamp(totalPrecip * 6, 0, 60);

        var tempPenalty = avgTemp < 8 ? (8 - avgTemp) * 2.5 : avgTemp > 22 ? (avgTemp - 22) * 2.0 : 0;
        score -= (int)Math.Clamp(tempPenalty, 0, 25);

        var windPenalty = maxWind <= 8 ? 0 : (maxWind - 8) * 3;
        score -= (int)Math.Clamp(windPenalty, 0, 25);

        score = Math.Clamp(score, 0, 100);

        var rainRisk01 = Math.Clamp(totalPrecip / 10.0, 0, 1);

        Forecast = new WeekendForecastScore
        {
            Town = Location.Town,
            Latitude = Location.Latitude,
            Longitude = Location.Longitude,
            WeekendStart = DateOnly.FromDateTime(start.UtcDateTime),
            WeekendEnd = DateOnly.FromDateTime(end.UtcDateTime),
            AvgTemperatureC = Math.Round(avgTemp, 1),
            MaxWindSpeedMps = Math.Round(maxWind, 1),
            TotalPrecipMm = Math.Round(totalPrecip, 1),
            RainRisk01 = Math.Round(rainRisk01, 2),
            WeatherSuitabilityScore = score,
            Summary = $"Avg {Math.Round(avgTemp, 1)}°C, wind up to {Math.Round(maxWind, 1)} m/s, precip {Math.Round(totalPrecip, 1)} mm"
        };
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetNextWeekendUtc()
    {
        var now = DateTimeOffset.UtcNow;
        var daysUntilSaturday = ((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilSaturday == 0) daysUntilSaturday = 7;
        var saturday = now.Date.AddDays(daysUntilSaturday);
        var start = new DateTimeOffset(saturday, TimeSpan.Zero);
        var end = start.AddDays(2).AddMinutes(-1);
        return (start, end);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "workflow-builder/1.0 (forecast; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private sealed class LocationForecastResponse
    {
        [JsonPropertyName("properties")] public Properties? Properties { get; set; }
    }

    private sealed class Properties
    {
        [JsonPropertyName("timeseries")] public List<TimeSeries> Timeseries { get; set; } = new();
    }

    private sealed class TimeSeries
    {
        [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
        [JsonPropertyName("data")] public Data? Data { get; set; }
    }

    private sealed class Data
    {
        [JsonPropertyName("instant")] public Instant? Instant { get; set; }
        [JsonPropertyName("next_1_hours")] public NextHours? Next1Hours { get; set; }
        [JsonPropertyName("next_6_hours")] public NextHours? Next6Hours { get; set; }
        [JsonPropertyName("next_12_hours")] public NextHours? Next12Hours { get; set; }
    }

    private sealed class Instant
    {
        [JsonPropertyName("details")] public InstantDetails? Details { get; set; }
    }

    private sealed class InstantDetails
    {
        [JsonPropertyName("air_temperature")] public double? AirTemperature { get; set; }
        [JsonPropertyName("wind_speed")] public double? WindSpeed { get; set; }
    }

    private sealed class NextHours
    {
        [JsonPropertyName("details")] public NextHoursDetails? Details { get; set; }
    }

    private sealed class NextHoursDetails
    {
        [JsonPropertyName("precipitation_amount")] public double? PrecipitationAmount { get; set; }
    }
}
