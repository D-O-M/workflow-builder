using Backend.Plugins.Harness;

namespace MunicipalReadinessAdvisor;

public sealed class MunicipalReadinessAdvisorPlugin : IPlugin
{
    public required MunicipalReadinessInput Input { get; init; }
    public MunicipalReadinessResult Recommendation { get; set; } = default!;

    public Task Execute()
    {
        var locationLabel = Input.LocationLabel.Trim();
        if (string.IsNullOrWhiteSpace(locationLabel))
        {
            throw new InvalidDataException("LocationLabel is required.");
        }

        var sensitivity = Input.SensitivityMultiplier <= 0 ? 1.0 : Input.SensitivityMultiplier;
        var goThreshold = Input.GoThreshold <= 0 ? 78 : Input.GoThreshold;
        var watchThreshold = Input.WatchThreshold <= 0 ? 55 : Input.WatchThreshold;

        if (watchThreshold > goThreshold)
        {
            throw new InvalidDataException("WatchThreshold cannot exceed GoThreshold.");
        }

        var weightedScore =
            (Input.WeatherSuitabilityScore * 0.45) +
            (Input.HolidayPressureScore * 0.15) +
            (Input.DaylightScore * 0.15) +
            (Input.AirQualityScore * 0.25);

        var readinessScore = (int)Math.Round(Math.Clamp(weightedScore * sensitivity, 0, 100));
        var band = readinessScore >= goThreshold
            ? "ScaleUp"
            : readinessScore >= watchThreshold
                ? "Prepare"
                : "Hold";

        var action = band switch
        {
            "ScaleUp" => "Increase staffing, publish visitor messaging, and prepare overflow plans.",
            "Prepare" => "Monitor conditions closely and stage a light readiness uplift.",
            _ => "Hold baseline operations and keep event messaging neutral."
        };

        Recommendation = new MunicipalReadinessResult
        {
            LocationLabel = locationLabel,
            ReadinessScore = readinessScore,
            RecommendationBand = band,
            RecommendedAction = action,
            Summary =
                $"Weather {Input.WeatherSuitabilityScore}, holiday pressure {Input.HolidayPressureScore}, daylight {Input.DaylightScore}, air quality {Input.AirQualityScore}. {Input.WeatherSummary} {Input.HolidaySummary} {Input.DaylightSummary} {Input.AirQualitySummary}"
        };

        return Task.CompletedTask;
    }
}
