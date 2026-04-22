using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace PublicHolidayWindow;

public sealed class PublicHolidayWindowPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required HolidayWindowInput Input { get; init; }
    public HolidayWindowResult WindowHoliday { get; set; } = default!;

    public PublicHolidayWindowPlugin()
        : this(CreateHttpClient())
    {
    }

    public PublicHolidayWindowPlugin(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task Execute()
    {
        var locationLabel = Input.LocationLabel.Trim();
        var countryCode = Input.CountryCode.Trim().ToUpperInvariant();
        var start = ParseIsoDate(Input.WindowStartIsoDate, nameof(Input.WindowStartIsoDate));
        var end = ParseIsoDate(Input.WindowEndIsoDate, nameof(Input.WindowEndIsoDate));

        if (string.IsNullOrWhiteSpace(locationLabel))
        {
            throw new InvalidDataException("LocationLabel is required.");
        }

        if (string.IsNullOrWhiteSpace(countryCode))
        {
            throw new InvalidDataException("CountryCode is required.");
        }

        if (end < start)
        {
            throw new InvalidDataException("WindowEndIsoDate must be on or after WindowStartIsoDate.");
        }

        var holidays = new List<PublicHoliday>();
        for (var year = start.Year; year <= end.Year; year++)
        {
            var url = $"https://date.nager.at/api/v3/PublicHolidays/{year}/{countryCode}";
            var yearHolidays = await _httpClient.GetFromJsonAsync<List<PublicHoliday>>(url) ?? [];
            holidays.AddRange(yearHolidays);
        }

        var windowHolidays = holidays
            .Select(holiday => new
            {
                Holiday = holiday,
                Date = DateOnly.ParseExact(holiday.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture)
            })
            .Where(candidate => candidate.Date >= start && candidate.Date <= end)
            .OrderBy(candidate => candidate.Date)
            .ToList();

        var headline = windowHolidays.FirstOrDefault();
        var count = windowHolidays.Count;
        var score = count switch
        {
            <= 0 => 20,
            1 => 62,
            2 => 82,
            _ => 95
        };

        WindowHoliday = new HolidayWindowResult
        {
            LocationLabel = locationLabel,
            CountryCode = countryCode,
            WindowStartIsoDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WindowEndIsoDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            HolidayCount = count,
            HeadlineHoliday = headline?.Holiday.LocalName
                ?? headline?.Holiday.Name
                ?? string.Empty,
            HolidayPressureScore = score,
            Summary = count == 0
                ? "No public holidays fall inside the selected execution window."
                : $"{count} public holiday(s) in window; first is {(headline?.Holiday.LocalName ?? headline?.Holiday.Name)} on {headline?.Date:yyyy-MM-dd}."
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (holiday-window; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class PublicHoliday
    {
        [JsonPropertyName("date")]
        public string Date { get; set; } = "";

        [JsonPropertyName("localName")]
        public string? LocalName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
