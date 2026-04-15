using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace WeekendHolidayDemand;

public sealed class HolidayDemandPlugin : IPlugin
{
    public required HolidayDemandInput Input { get; init; }
    public HolidayDemandResult? Demand { get; set; }

    private static readonly HttpClient _http = CreateHttpClient();

    public async Task Execute()
    {
        if (string.IsNullOrWhiteSpace(Input.CountryCode))
            throw new InvalidDataException("CountryCode is required.");

        var url = $"https://date.nager.at/api/v3/PublicHolidays/{Input.Year}/{Input.CountryCode}";
        var holidays = await _http.GetFromJsonAsync<List<Holiday>>(url) ?? [];

        var start = Input.WeekendStart;
        var end = Input.WeekendEnd;
        var windowStart = start.AddDays(-2);
        var windowEnd = end.AddDays(2);

        var nearby = holidays
            .Select(h => new { Holiday = h, Date = DateOnly.FromDateTime(h.Date) })
            .Where(x => x.Date >= windowStart && x.Date <= windowEnd)
            .OrderBy(x => x.Date)
            .FirstOrDefault();

        var adjustment = nearby is null ? 0.0 : 0.15;

        Demand = new HolidayDemandResult
        {
            Town = Input.Town,
            CountryCode = Input.CountryCode.ToUpperInvariant(),
            WeekendStart = start,
            WeekendEnd = end,
            HolidayNearby = nearby is not null,
            HolidayName = nearby?.Holiday.LocalName ?? nearby?.Holiday.Name,
            DemandAdjustment = adjustment,
            Summary = nearby is null
                ? "No nearby public holiday detected."
                : $"Public holiday near weekend: {nearby.Holiday.LocalName ?? nearby.Holiday.Name} ({nearby.Date:yyyy-MM-dd}) -> +{adjustment:P0} demand"
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "workflow-builder/1.0 (holidays; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class Holiday
    {
        [JsonPropertyName("date")] public DateTime Date { get; set; }
        [JsonPropertyName("localName")] public string? LocalName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
    }
}
