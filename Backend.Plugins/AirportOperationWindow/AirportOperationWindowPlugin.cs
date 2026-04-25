using Backend.Plugins.Harness;

namespace AirportOperationWindow;

public sealed class AirportOperationWindowPlugin : IPlugin
{
    public required AirportOperationWindowInput Input { get; init; }
    public AirportOperationWindowResult Window { get; set; } = default!;

    public Task Execute()
    {
        var airportCode = Input.AirportCode.Trim().ToUpperInvariant();
        var start = Input.WindowStartIsoDate;
        var end = Input.WindowEndIsoDate;

        if (string.IsNullOrWhiteSpace(airportCode))
        {
            throw new InvalidDataException("AirportCode is required.");
        }

        if (start == default || end == default)
        {
            throw new InvalidDataException("WindowStartIsoDate and WindowEndIsoDate are required.");
        }

        if (end < start)
        {
            throw new InvalidDataException("WindowEndIsoDate must be on or after WindowStartIsoDate.");
        }

        Window = new AirportOperationWindowResult
        {
            AirportCode = airportCode,
            WindowStartIsoDate = start,
            WindowEndIsoDate = end,
            WindowLabel = $"{start:yyyy-MM-dd} to {end:yyyy-MM-dd}"
        };

        return Task.CompletedTask;
    }
}
