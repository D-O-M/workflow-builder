using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Plugins.Harness;

namespace PublicDaylightWindow;

public sealed class PublicDaylightWindowPlugin : IPlugin
{
    private readonly HttpClient _httpClient;

    public required DaylightWindowInput Input { get; init; }
    public DaylightWindowResult Daylight { get; set; } = default!;

    public PublicDaylightWindowPlugin()
        : this(CreateHttpClient())
    {
    }

    public PublicDaylightWindowPlugin(HttpClient httpClient)
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

        var daylightHours = new List<double>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var url = $"https://api.sunrise-sunset.org/json?lat={Input.Latitude.ToString(CultureInfo.InvariantCulture)}&lng={Input.Longitude.ToString(CultureInfo.InvariantCulture)}&date={date:yyyy-MM-dd}&formatted=0";
            var response = await _httpClient.GetFromJsonAsync<SunResponse>(url);
            if (!string.Equals(response?.Status, "OK", StringComparison.OrdinalIgnoreCase) ||
                response?.Results?.DayLength is null)
            {
                throw new InvalidDataException($"Sunrise-sunset response missing day length for {date:yyyy-MM-dd}.");
            }

            var dayLengthSeconds = response.Results?.DayLength;
            if (!dayLengthSeconds.HasValue)
            {
                throw new InvalidDataException($"Sunrise-sunset response missing day length for {date:yyyy-MM-dd}.");
            }

            daylightHours.Add(dayLengthSeconds.Value / 3600d);
        }

        var averageHours = daylightHours.Average();
        var score = (int)Math.Round(Math.Clamp((averageHours - 8.0) / 6.0 * 100.0, 0, 100));

        Daylight = new DaylightWindowResult
        {
            LocationLabel = locationLabel,
            WindowStartIsoDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WindowEndIsoDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            AverageDaylightHours = Math.Round(averageHours, 2),
            DaylightScore = score,
            Summary = $"Average daylight across the execution window is {Math.Round(averageHours, 1)} hours."
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("workflow-builder/1.0 (daylight-window; contact=admin@example.invalid)");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class SunResponse
    {
        [JsonPropertyName("results")]
        public SunResults? Results { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    private sealed class SunResults
    {
        [JsonPropertyName("day_length")]
        public double? DayLength { get; set; }
    }
}
