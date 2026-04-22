using Backend.Plugins.Harness;

namespace AirportDisruptionAdvisor;

public sealed class AirportDisruptionAdvisorPlugin : IPlugin
{
    public required AirportDisruptionAdvisorInput Input { get; init; }
    public AirportDisruptionAdvisorResult Advisory { get; set; } = default!;

    public Task Execute()
    {
        var airportCode = Input.AirportCode.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(airportCode) || string.IsNullOrWhiteSpace(Input.AirportName))
        {
            throw new InvalidDataException("AirportCode and AirportName are required.");
        }

        var sensitivity = Input.SensitivityMultiplier <= 0 ? 1.0 : Input.SensitivityMultiplier;
        var escalateThreshold = Input.EscalateThreshold <= 0 ? 70 : Input.EscalateThreshold;
        var monitorThreshold = Input.MonitorThreshold <= 0 ? 45 : Input.MonitorThreshold;

        if (monitorThreshold > escalateThreshold)
        {
            throw new InvalidDataException("MonitorThreshold cannot exceed EscalateThreshold.");
        }

        var daylightRelief = Math.Clamp((Input.AverageDaylightHours - 8.0) * 2.5, 0, 20);
        var baseScore = (Input.OperationalWeatherRiskScore * 0.60) + (Input.TrafficPressureScore * 0.30) + (Math.Min(Input.GroundAircraftCount, 20) * 0.5) - daylightRelief;
        var disruptionScore = (int)Math.Round(Math.Clamp(baseScore * sensitivity, 0, 100));

        var band = disruptionScore >= escalateThreshold
            ? "Escalate"
            : disruptionScore >= monitorThreshold
                ? "Monitor"
                : "Stable";

        var action = band switch
        {
            "Escalate" => "Trigger disruption playbooks, alert operations leads, and prepare passenger comms.",
            "Monitor" => "Hold additional gate or staffing buffers ready and monitor the next observation cycle.",
            _ => "Maintain normal ops and keep passive monitoring in place."
        };

        Advisory = new AirportDisruptionAdvisorResult
        {
            AirportCode = airportCode,
            AirportName = Input.AirportName,
            DisruptionScore = disruptionScore,
            AdvisoryBand = band,
            RecommendedAction = action,
            Summary = $"{Input.AirportName} for {Input.WindowLabel}: weather risk {Input.OperationalWeatherRiskScore}, traffic pressure {Input.TrafficPressureScore}, nearby aircraft {Input.NearbyAircraftCount}. {Input.WeatherSummary} {Input.DaylightSummary}"
        };

        return Task.CompletedTask;
    }
}
