using Backend.Plugins.Harness;

namespace DeliveryWindowAdvisor;

public sealed class DeliveryWindowAdvisorPlugin : IPlugin
{
    public required DeliveryWindowAdvisorInput Input { get; init; }
    public DeliveryWindowAdvisorResult Decision { get; set; } = default!;

    public Task Execute()
    {
        if (string.IsNullOrWhiteSpace(Input.ShipmentLabel) || string.IsNullOrWhiteSpace(Input.DestinationLabel))
        {
            throw new InvalidDataException("ShipmentLabel and DestinationLabel are required.");
        }

        var targetRate = Input.TargetExchangeRate <= 0 ? Input.ExchangeRate : Input.TargetExchangeRate;
        var sensitivity = Input.SensitivityMultiplier <= 0 ? 1.0 : Input.SensitivityMultiplier;
        var goThreshold = Input.GoThreshold <= 0 ? 72 : Input.GoThreshold;
        var watchThreshold = Input.WatchThreshold <= 0 ? 50 : Input.WatchThreshold;

        if (watchThreshold > goThreshold)
        {
            throw new InvalidDataException("WatchThreshold cannot exceed GoThreshold.");
        }

        var fxPenalty = Math.Clamp(Math.Abs(Input.ExchangeRate - targetRate) / targetRate * 100.0, 0, 100);
        var daylightBonus = Math.Clamp(Input.AverageDaylightHours / 14.0 * 12.0, 0, 12);

        var baseScore = 100.0
            - (Input.WeatherRiskScore * 0.42)
            - (Input.SupplierHolidayPressureScore * 0.11)
            - (Input.ReceiverHolidayPressureScore * 0.11)
            - (fxPenalty * 0.20)
            + daylightBonus;

        var decisionScore = (int)Math.Round(Math.Clamp(baseScore * sensitivity, 0, 100));
        var band = decisionScore >= goThreshold
            ? "Proceed"
            : decisionScore >= watchThreshold
                ? "Monitor"
                : "Delay";

        var action = band switch
        {
            "Proceed" => "Lock the delivery window, confirm carriers, and release procurement comms.",
            "Monitor" => "Keep the slot provisional and watch weather or FX moves before committing.",
            _ => "Defer the window and replan when trading or operating conditions improve."
        };

        Decision = new DeliveryWindowAdvisorResult
        {
            ShipmentLabel = Input.ShipmentLabel,
            DecisionBand = band,
            DecisionScore = decisionScore,
            RecommendedAction = action,
            Summary =
                $"{Input.DestinationLabel}: weather risk {Input.WeatherRiskScore}, supplier holiday {Input.SupplierHolidayPressureScore}, receiver holiday {Input.ReceiverHolidayPressureScore}, FX {Input.BaseCurrencyCode}/{Input.QuoteCurrencyCode} at {Input.ExchangeRate:F4}. {Input.WeatherSummary} {Input.DaylightSummary} {Input.SupplierHolidaySummary} {Input.ReceiverHolidaySummary} {Input.FxSummary}"
        };

        return Task.CompletedTask;
    }
}
