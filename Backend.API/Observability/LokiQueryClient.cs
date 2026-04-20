using System.Globalization;
using System.Text.Json;

namespace Backend.API.Observability;

public enum LokiQueryDirection
{
    Forward,
    Backward
}

public sealed record LokiLogRecord(
    DateTimeOffset Timestamp,
    string Cursor,
    string Message,
    IReadOnlyDictionary<string, string> Labels);

public sealed class LokiQueryClient
{
    private const int MaxEntriesPerQuery = 5000;
    private readonly HttpClient _httpClient;

    public LokiQueryClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<LokiLogRecord>> QueryRangeAsync(
        string query,
        DateTimeOffset start,
        DateTimeOffset end,
        int limit,
        LokiQueryDirection direction,
        CancellationToken cancellationToken)
    {
        var normalizedLimit = Math.Max(1, limit);
        var records = new List<LokiLogRecord>();
        var currentStart = start;
        var currentEnd = end;

        while (records.Count < normalizedLimit)
        {
            var pageLimit = Math.Min(MaxEntriesPerQuery, normalizedLimit - records.Count);
            var page = await QueryRangePageAsync(
                query,
                currentStart,
                currentEnd,
                pageLimit,
                direction,
                cancellationToken);

            if (page.Count == 0)
            {
                break;
            }

            records.AddRange(page);

            if (page.Count < pageLimit)
            {
                break;
            }

            if (direction == LokiQueryDirection.Forward)
            {
                var lastTimestamp = page[^1].Timestamp;
                if (lastTimestamp >= currentEnd)
                {
                    break;
                }

                currentStart = lastTimestamp.AddTicks(1);
            }
            else
            {
                var lastTimestamp = page[^1].Timestamp;
                if (lastTimestamp <= currentStart)
                {
                    break;
                }

                currentEnd = lastTimestamp.AddTicks(-1);
            }
        }

        return direction == LokiQueryDirection.Forward
            ? records.OrderBy(record => record.Timestamp).Take(normalizedLimit).ToList()
            : records.OrderByDescending(record => record.Timestamp).Take(normalizedLimit).ToList();
    }

    private async Task<List<LokiLogRecord>> QueryRangePageAsync(
        string query,
        DateTimeOffset start,
        DateTimeOffset end,
        int limit,
        LokiQueryDirection direction,
        CancellationToken cancellationToken)
    {
        var requestUri =
            $"/loki/api/v1/query_range?query={Uri.EscapeDataString(query)}" +
            $"&start={Uri.EscapeDataString(ToUnixNanoseconds(start))}" +
            $"&end={Uri.EscapeDataString(ToUnixNanoseconds(end))}" +
            $"&limit={limit}" +
            $"&direction={(direction == LokiQueryDirection.Forward ? "forward" : "backward")}";

        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var dataElement) ||
            !dataElement.TryGetProperty("result", out var resultElement))
        {
            return [];
        }

        var records = new List<LokiLogRecord>();
        foreach (var streamElement in resultElement.EnumerateArray())
        {
            var labels = streamElement.TryGetProperty("stream", out var labelElement)
                ? labelElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            if (!streamElement.TryGetProperty("values", out var valuesElement))
            {
                continue;
            }

            foreach (var valueElement in valuesElement.EnumerateArray())
            {
                if (valueElement.GetArrayLength() < 2)
                {
                    continue;
                }

                var cursor = valueElement[0].GetString();
                var message = valueElement[1].GetString();
                if (string.IsNullOrWhiteSpace(cursor) || message is null)
                {
                    continue;
                }

                records.Add(new LokiLogRecord(
                    FromUnixNanoseconds(cursor),
                    cursor,
                    message,
                    labels));
            }
        }

        return direction == LokiQueryDirection.Forward
            ? records.OrderBy(record => record.Timestamp).ToList()
            : records.OrderByDescending(record => record.Timestamp).ToList();
    }

    public static string ToUnixNanoseconds(DateTimeOffset timestamp)
    {
        var ticksSinceUnixEpoch = timestamp.UtcDateTime.Ticks - DateTimeOffset.UnixEpoch.Ticks;
        return (ticksSinceUnixEpoch * 100L).ToString(CultureInfo.InvariantCulture);
    }

    public static DateTimeOffset FromUnixNanoseconds(string nanoseconds)
    {
        var totalNanoseconds = long.Parse(nanoseconds, CultureInfo.InvariantCulture);
        var ticks = totalNanoseconds / 100L;
        return new DateTimeOffset(DateTimeOffset.UnixEpoch.Ticks + ticks, TimeSpan.Zero);
    }
}
