using Backend.Plugins.Harness;

namespace AirportWeatherRisk;

public sealed class AirportWeatherRiskPlugin : IPlugin
{
    public required AirportWeatherRiskInput Input { get; init; }
    public AirportWeatherRiskResult Risk { get; set; } = default!;

    public Task Execute()
    {
        var airportCode = Input.AirportCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(airportCode))
        {
            throw new InvalidDataException("AirportCode is required.");
        }

        var metarPenalty = CategoryPenalty(Input.MetarFlightCategory);
        var tafPenalty = CategoryPenalty(Input.TafWorstFlightCategory);
        var windPenalty = Math.Clamp((Math.Max(Input.MetarWindSpeedKnots, Input.TafForecastWindSpeedKnots) - 20.0) * 1.5, 0, 25);
        var visibilityPenalty = Math.Clamp((3.0 - Math.Min(Input.MetarVisibilityMiles, Input.TafForecastVisibilityMiles)) * 10.0, 0, 30);
        var ceilingPenalty = Math.Clamp((1000.0 - Math.Min(Input.MetarCeilingFeet, Input.TafForecastCeilingFeet)) / 40.0, 0, 25);
        var advisoryPenalty = Math.Clamp((Input.AdvisoryCount * 5.0) + (Input.HighestAdvisorySeverityScore * 4.0), 0, 30);

        var riskScore = (int)Math.Round(Math.Clamp(metarPenalty + tafPenalty + windPenalty + visibilityPenalty + ceilingPenalty + advisoryPenalty, 0, 100));

        Risk = new AirportWeatherRiskResult
        {
            AirportCode = airportCode,
            OperationalWeatherRiskScore = riskScore,
            Summary = $"METAR {Input.MetarFlightCategory}, TAF {Input.TafWorstFlightCategory}, advisories {Input.AdvisoryCount}, max wind {Math.Max(Input.MetarWindSpeedKnots, Input.TafForecastWindSpeedKnots):F0}kt."
        };

        return Task.CompletedTask;
    }

    private static int CategoryPenalty(string category) => category switch
    {
        "LIFR" => 42,
        "IFR" => 28,
        "MVFR" => 14,
        _ => 0
    };
}
