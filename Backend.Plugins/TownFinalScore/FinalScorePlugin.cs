using Backend.Plugins.Harness;

namespace TownFinalScore;

public sealed class FinalScorePlugin : IPlugin
{
    public required FinalScoreInput Input { get; init; }
    public FinalTownScore? Result { get; set; }

    public Task Execute()
    {
        var weather = Input;
        var demand = Input;

        var adjusted = (int)Math.Round(weather.WeatherSuitabilityScore * (1.0 + demand.DemandAdjustment));
        adjusted = Math.Clamp(adjusted, 0, 100);

        Result = new FinalTownScore
        {
            Town = weather.Town,
            WeatherScore = weather.WeatherSuitabilityScore,
            DemandAdjustment = demand.DemandAdjustment,
            FinalScore = adjusted,
            ForecastSummary = weather.Summary,
            HolidaySummary = demand.Summary,
            Explanation = "FinalScore = clamp(WeatherSuitabilityScore * (1 + DemandAdjustment), 0..100). " +
                          "Weather considers precipitation, temperature comfort, and wind. Demand increases near public holidays."
        };

        return Task.CompletedTask;
    }
}
