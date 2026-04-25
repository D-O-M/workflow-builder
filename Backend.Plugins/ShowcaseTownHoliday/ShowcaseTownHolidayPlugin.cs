using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace ShowcaseTownHoliday;

public sealed class ShowcaseTownHolidayPlugin : IPlugin
{
    private static readonly TimeSpan DemonstrationDelay = TimeSpan.FromSeconds(5);

    public required TownWeekendForecast TownWeekendForecast { get; init; }
    public TownWeekendHolidayContext TownWeekendHolidayContext { get; set; } = new();

    private static readonly HttpClient _http = CreateHttpClient();

    public async Task Execute()
    {
        await Task.Delay(DemonstrationDelay);

        if (string.IsNullOrWhiteSpace(TownWeekendForecast.ResolvedCountryCode))
        {
            TownWeekendHolidayContext = ShowcaseTownHolidayFallback.CreateUnavailableContext(
                TownWeekendForecast,
                "Holiday lookup skipped because the resolved country code was unavailable.");
            return;
        }

        var weekendStart = TownWeekendForecast.WeekendStartIsoDate;
        var weekendEnd = TownWeekendForecast.WeekendEndIsoDate;

        if (weekendStart == default || weekendEnd == default)
        {
            throw new InvalidDataException("WeekendStartIsoDate and WeekendEndIsoDate are required.");
        }

        if (weekendEnd < weekendStart)
        {
            throw new InvalidDataException("WeekendEndIsoDate must be on or after WeekendStartIsoDate.");
        }

        try
        {
            var yearsToCheck = new HashSet<int> { weekendStart.Year, weekendEnd.Year };
            var publicHolidays = new List<Holiday>();

            foreach (var year in yearsToCheck.OrderBy(value => value))
            {
                var url =
                    $"https://date.nager.at/api/v3/PublicHolidays/{year}/{TownWeekendForecast.ResolvedCountryCode.ToUpperInvariant()}";
                var holidaysForYear = await _http.GetFromJsonAsync<List<Holiday>>(url) ?? [];
                publicHolidays.AddRange(holidaysForYear);
            }

            var lookupWindowStart = weekendStart.AddDays(-2);
            var lookupWindowEnd = weekendEnd.AddDays(2);

            var nearbyHoliday = publicHolidays
                .Select(holiday => new
                {
                    Holiday = holiday,
                    HolidayDate = DateOnly.FromDateTime(holiday.Date)
                })
                .Where(entry => entry.HolidayDate >= lookupWindowStart && entry.HolidayDate <= lookupWindowEnd)
                .OrderBy(entry => entry.HolidayDate)
                .FirstOrDefault();

            var holidayDemandAdjustmentPct = nearbyHoliday is null ? 0 : 0.15;

            TownWeekendHolidayContext = new TownWeekendHolidayContext
            {
                RequestedTownName = TownWeekendForecast.RequestedTownName,
                ResolvedTownName = TownWeekendForecast.ResolvedTownName,
                ResolvedDisplayName = TownWeekendForecast.ResolvedDisplayName,
                ResolvedCountryCode = TownWeekendForecast.ResolvedCountryCode.ToUpperInvariant(),
                Latitude = TownWeekendForecast.Latitude,
                Longitude = TownWeekendForecast.Longitude,
                WeekendStartIsoDate = TownWeekendForecast.WeekendStartIsoDate,
                WeekendEndIsoDate = TownWeekendForecast.WeekendEndIsoDate,
                AverageTemperatureC = TownWeekendForecast.AverageTemperatureC,
                MaximumWindSpeedMps = TownWeekendForecast.MaximumWindSpeedMps,
                TotalPrecipitationMm = TownWeekendForecast.TotalPrecipitationMm,
                RainRisk01 = TownWeekendForecast.RainRisk01,
                WeatherSuitabilityScore = TownWeekendForecast.WeatherSuitabilityScore,
                WeatherSummary = TownWeekendForecast.WeatherSummary,
                HolidayLookupSucceeded = true,
                NearbyHolidayDetected = nearbyHoliday is not null,
                NearbyHolidayName = nearbyHoliday?.Holiday.LocalName ?? nearbyHoliday?.Holiday.Name ?? "",
                HolidayDemandAdjustmentPct = holidayDemandAdjustmentPct,
                HolidaySummary = nearbyHoliday is null
                    ? "No nearby public holiday was detected for that weekend."
                    : $"Public holiday nearby: {nearbyHoliday.Holiday.LocalName ?? nearbyHoliday.Holiday.Name} on {nearbyHoliday.HolidayDate:yyyy-MM-dd}, applying a {holidayDemandAdjustmentPct:P0} demand lift."
            };
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or TaskCanceledException
            or JsonException
            or NotSupportedException)
        {
            TownWeekendHolidayContext = ShowcaseTownHolidayFallback.CreateUnavailableContext(
                TownWeekendForecast,
                "Holiday lookup was unavailable, so the workflow continued without any holiday demand adjustment.");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "workflow-builder/1.0 (showcase-town-holiday; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class Holiday
    {
        [JsonPropertyName("date")] public DateTime Date { get; set; }
        [JsonPropertyName("localName")] public string? LocalName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
