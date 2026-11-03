using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace AirportTafOutlook;

public sealed class AirportTafOutlookPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required AirportTafInput Input { get; init; }
    public AirportTafResult Taf { get; set; } = default!;

    [ActivatorUtilitiesConstructor]
    public AirportTafOutlookPlugin()
        : this(CreateHttpClient())
    {
    }

    public AirportTafOutlookPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task Execute()
    {
        var stationCode = Input.StationCode.Trim().ToUpperInvariant();
        var start = Input.WindowStartIsoDate;
        var end = Input.WindowEndIsoDate;
        if (string.IsNullOrWhiteSpace(stationCode))
        {
            throw new InvalidDataException("StationCode is required.");
        }

        if (start == default || end == default)
        {
            throw new InvalidDataException("WindowStartIsoDate and WindowEndIsoDate are required.");
        }

        if (end < start)
        {
            throw new InvalidDataException("WindowEndIsoDate must be on or after WindowStartIsoDate.");
        }

        var startUtc = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(end.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        var url = $"https://aviationweather.gov/api/data/taf?ids={stationCode}&format=json";
        var response = await _httpClient.GetFromJsonAsync<List<TafForecastEnvelope>>(url);
        var taf = response?.FirstOrDefault() ?? throw new InvalidDataException($"No TAF was returned for {stationCode}.");

        var overlapping = taf.Forecasts
            .Where(forecast => forecast.TimeFrom <= endUtc.ToUnixTimeSeconds() && forecast.TimeTo >= startUtc.ToUnixTimeSeconds())
            .ToList();

        if (overlapping.Count == 0)
        {
            throw new InvalidDataException($"No TAF forecast segments overlapped the window for {stationCode}.");
        }

        var ranked = overlapping
            .Select(forecast =>
            {
                var visibility = ParseVisibilityMiles(forecast.Visibility);
                var ceiling = forecast.Clouds
                    .Where(cloud => string.Equals(cloud.Cover, "BKN", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(cloud.Cover, "OVC", StringComparison.OrdinalIgnoreCase))
                    .Select(cloud => cloud.Base)
                    .DefaultIfEmpty(12000)
                    .Min();

                var category = DetermineFlightCategory(visibility, ceiling);
                return new
                {
                    Forecast = forecast,
                    Category = category,
                    Visibility = visibility,
                    Ceiling = ceiling
                };
            })
            .OrderBy(candidate => CategoryRank(candidate.Category))
            .ThenBy(candidate => candidate.Visibility)
            .ThenBy(candidate => candidate.Ceiling)
            .ToList();

        var worst = ranked.First();

        Taf = new AirportTafResult
        {
            StationCode = stationCode,
            WorstFlightCategory = worst.Category,
            ForecastWindSpeedKnots = Math.Round(overlapping.Max(forecast => forecast.WindSpeed ?? 0), 1),
            ForecastVisibilityMiles = Math.Round(ranked.Min(candidate => candidate.Visibility), 1),
            ForecastCeilingFeet = ranked.Min(candidate => candidate.Ceiling),
            Summary = taf.RawTaf ?? $"{stationCode} TAF worst category {worst.Category}"
        };
    }

    private static double ParseVisibilityMiles(object? visibility)
    {
        if (visibility is null)
        {
            return 10;
        }

        var visibilityText = visibility.ToString() ?? string.Empty;
        visibilityText = visibilityText.Replace("SM", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("+", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return double.TryParse(visibilityText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 10;
    }

    private static string DetermineFlightCategory(double visibilityMiles, int ceilingFeet)
    {
        if (visibilityMiles < 1 || ceilingFeet < 500) return "LIFR";
        if (visibilityMiles < 3 || ceilingFeet < 1000) return "IFR";
        if (visibilityMiles < 5 || ceilingFeet < 3000) return "MVFR";
        return "VFR";
    }

    private static int CategoryRank(string category) => category switch
    {
        "LIFR" => 0,
        "IFR" => 1,
        "MVFR" => 2,
        _ => 3
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (airport-taf; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class TafForecastEnvelope
    {
        [JsonPropertyName("rawTAF")]
        public string? RawTaf { get; set; }

        [JsonPropertyName("fcsts")]
        public List<TafForecast> Forecasts { get; set; } = [];
    }

    private sealed class TafForecast
    {
        [JsonPropertyName("timeFrom")]
        public long TimeFrom { get; set; }

        [JsonPropertyName("timeTo")]
        public long TimeTo { get; set; }

        [JsonPropertyName("wspd")]
        public double? WindSpeed { get; set; }

        [JsonPropertyName("visib")]
        public object? Visibility { get; set; }

        [JsonPropertyName("clouds")]
        public List<TafCloudLayer> Clouds { get; set; } = [];
    }

    private sealed class TafCloudLayer
    {
        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("base")]
        public int Base { get; set; }
    }
}
