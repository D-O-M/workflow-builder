using System.Globalization;
using Backend.Plugins.Harness;

namespace AirportOperationWindow;

public sealed class AirportOperationWindowPlugin : IPlugin
{
    public required AirportOperationWindowInput Input { get; init; }
    public AirportOperationWindowResult Window { get; set; } = default!;

    public Task Execute()
    {
        var airportCode = Input.AirportCode.Trim().ToUpperInvariant();
        var start = ParseIsoDate(Input.WindowStartIsoDate, nameof(Input.WindowStartIsoDate));
        var end = ParseIsoDate(Input.WindowEndIsoDate, nameof(Input.WindowEndIsoDate));

        if (string.IsNullOrWhiteSpace(airportCode))
        {
            throw new InvalidDataException("AirportCode is required.");
        }

        if (end < start)
        {
            throw new InvalidDataException("WindowEndIsoDate must be on or after WindowStartIsoDate.");
        }

        Window = new AirportOperationWindowResult
        {
            AirportCode = airportCode,
            WindowStartIsoDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WindowEndIsoDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            WindowLabel = $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}"
        };

        return Task.CompletedTask;
    }

    private static DateOnly ParseIsoDate(string value, string propertyName)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new InvalidDataException($"{propertyName} must use yyyy-MM-dd format.");
        }

        return parsed;
    }
}
