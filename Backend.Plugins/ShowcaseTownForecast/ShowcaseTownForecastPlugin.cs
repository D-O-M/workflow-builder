using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace ShowcaseTownForecast;

public sealed class ShowcaseTownForecastPlugin : IPlugin
{
    private static readonly TimeSpan DemonstrationDelay = TimeSpan.FromSeconds(5);

    public required ResolvedTown ResolvedTown { get; init; }
    public TownWeekendForecast TownWeekendForecast { get; set; } = new();

    private static readonly HttpClient _http = CreateHttpClient();

    public async Task Execute()
    {
        await Task.Delay(DemonstrationDelay);

        var weekendWindow = GetNextWeekendUtc(DateTimeOffset.UtcNow);

        var url =
            $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={ResolvedTown.Latitude.ToString(CultureInfo.InvariantCulture)}&lon={ResolvedTown.Longitude.ToString(CultureInfo.InvariantCulture)}";
        var forecastResponse = await _http.GetFromJsonAsync<LocationForecastResponse>(url);
        if (forecastResponse?.Properties?.Timeseries is null || forecastResponse.Properties.Timeseries.Count == 0)
            throw new InvalidDataException("MET Norway response did not include any forecast timeseries.");

        var weekendPoints = forecastResponse.Properties.Timeseries
            .Where(point => point.Time >= weekendWindow.StartUtc && point.Time <= weekendWindow.EndUtc)
            .ToList();

        if (weekendPoints.Count == 0)
            throw new InvalidDataException("No forecast points were available for the next weekend window.");

        var temperatures = weekendPoints
            .Select(point => point.Data?.Instant?.Details?.AirTemperature)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        var windSpeeds = weekendPoints
            .Select(point => point.Data?.Instant?.Details?.WindSpeed)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        var precipitationValues = weekendPoints
            .Select(point =>
                point.Data?.Next1Hours?.Details?.PrecipitationAmount
                ?? point.Data?.Next6Hours?.Details?.PrecipitationAmount
                ?? point.Data?.Next12Hours?.Details?.PrecipitationAmount)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        var averageTemperature = temperatures.Count > 0 ? temperatures.Average() : 0;
        var maximumWindSpeed = windSpeeds.Count > 0 ? windSpeeds.Max() : 0;
        var totalPrecipitation = precipitationValues.Sum();
        var rainRisk01 = Math.Clamp(totalPrecipitation / 10.0, 0, 1);

        TownWeekendForecast = new TownWeekendForecast
        {
            RequestedTownName = ResolvedTown.RequestedTownName,
            ResolvedTownName = ResolvedTown.ResolvedTownName,
            ResolvedDisplayName = ResolvedTown.ResolvedDisplayName,
            ResolvedCountryCode = ResolvedTown.ResolvedCountryCode,
            Latitude = ResolvedTown.Latitude,
            Longitude = ResolvedTown.Longitude,
            WeekendStartIsoDate = weekendWindow.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WeekendEndIsoDate = weekendWindow.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AverageTemperatureC = Math.Round(averageTemperature, 1),
            MaximumWindSpeedMps = Math.Round(maximumWindSpeed, 1),
            TotalPrecipitationMm = Math.Round(totalPrecipitation, 1),
            RainRisk01 = Math.Round(rainRisk01, 2),
            WeatherSuitabilityScore = ShowcaseTownWeatherScoring.CalculateWeatherSuitabilityScore(
                averageTemperature,
                totalPrecipitation,
                maximumWindSpeed),
            WeatherSummary =
                $"Next weekend in {ResolvedTown.ResolvedTownName}: avg {Math.Round(averageTemperature, 1)} C, wind up to {Math.Round(maximumWindSpeed, 1)} m/s, precipitation {Math.Round(totalPrecipitation, 1)} mm."
        };
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateOnly StartDate, DateOnly EndDate)
        GetNextWeekendUtc(DateTimeOffset nowUtc)
    {
        var daysUntilSaturday = ((int)DayOfWeek.Saturday - (int)nowUtc.DayOfWeek + 7) % 7;
        if (daysUntilSaturday == 0)
            daysUntilSaturday = 7;

        var saturday = nowUtc.UtcDateTime.Date.AddDays(daysUntilSaturday);
        var startUtc = new DateTimeOffset(saturday, TimeSpan.Zero);
        var endUtc = startUtc.AddDays(2).AddMinutes(-1);

        return (
            startUtc,
            endUtc,
            DateOnly.FromDateTime(startUtc.UtcDateTime),
            DateOnly.FromDateTime(endUtc.UtcDateTime));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "workflow-builder/1.0 (showcase-town-forecast; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    private sealed class LocationForecastResponse
    {
        [JsonPropertyName("properties")] public ForecastProperties? Properties { get; set; }
    }

    private sealed class ForecastProperties
    {
        [JsonPropertyName("timeseries")] public List<ForecastPoint> Timeseries { get; set; } = [];
    }

    private sealed class ForecastPoint
    {
        [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
        [JsonPropertyName("data")] public ForecastData? Data { get; set; }
    }

    private sealed class ForecastData
    {
        [JsonPropertyName("instant")] public InstantForecast? Instant { get; set; }
        [JsonPropertyName("next_1_hours")] public NextHoursForecast? Next1Hours { get; set; }
        [JsonPropertyName("next_6_hours")] public NextHoursForecast? Next6Hours { get; set; }
        [JsonPropertyName("next_12_hours")] public NextHoursForecast? Next12Hours { get; set; }
    }

    private sealed class InstantForecast
    {
        [JsonPropertyName("details")] public InstantForecastDetails? Details { get; set; }
    }

    private sealed class InstantForecastDetails
    {
        [JsonPropertyName("air_temperature")] public double? AirTemperature { get; set; }
        [JsonPropertyName("wind_speed")] public double? WindSpeed { get; set; }
    }

    private sealed class NextHoursForecast
    {
        [JsonPropertyName("details")] public NextHoursForecastDetails? Details { get; set; }
    }

    private sealed class NextHoursForecastDetails
    {
        [JsonPropertyName("precipitation_amount")] public double? PrecipitationAmount { get; set; }
    }
}
