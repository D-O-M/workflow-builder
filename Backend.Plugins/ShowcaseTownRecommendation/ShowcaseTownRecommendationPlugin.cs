using Backend.Plugins.Harness;

namespace ShowcaseTownRecommendation;

public sealed class ShowcaseTownRecommendationPlugin : IPlugin
{
    private static readonly TimeSpan DemonstrationDelay = TimeSpan.FromSeconds(5);

    public required TownWeekendHolidayContext TownWeekendHolidayContext { get; init; }
    public TownRecommendation TownRecommendation { get; set; } = new();

    public async Task Execute()
    {
        await Task.Delay(DemonstrationDelay);

        var finalRecommendationScore = ShowcaseTownRecommendationScoring.CalculateFinalRecommendationScore(
            TownWeekendHolidayContext.WeatherSuitabilityScore,
            TownWeekendHolidayContext.HolidayDemandAdjustmentPct);
        var recommendationBand = ShowcaseTownRecommendationScoring.GetRecommendationBand(finalRecommendationScore);

        TownRecommendation = new TownRecommendation
        {
            ResolvedTownName = TownWeekendHolidayContext.ResolvedTownName,
            ResolvedCountryCode = TownWeekendHolidayContext.ResolvedCountryCode,
            WeekendStartIsoDate = TownWeekendHolidayContext.WeekendStartIsoDate,
            WeekendEndIsoDate = TownWeekendHolidayContext.WeekendEndIsoDate,
            WeatherSuitabilityScore = TownWeekendHolidayContext.WeatherSuitabilityScore,
            HolidayDemandAdjustmentPct = TownWeekendHolidayContext.HolidayDemandAdjustmentPct,
            FinalRecommendationScore = finalRecommendationScore,
            RecommendationBand = recommendationBand,
            RecommendationSummary =
                $"{TownWeekendHolidayContext.ResolvedTownName} is a {recommendationBand} for {TownWeekendHolidayContext.WeekendStartIsoDate:yyyy-MM-dd} to {TownWeekendHolidayContext.WeekendEndIsoDate:yyyy-MM-dd}. Final score {finalRecommendationScore}/100.",
            WeatherSummary = TownWeekendHolidayContext.WeatherSummary,
            HolidaySummary = TownWeekendHolidayContext.HolidaySummary
        };
    }
}
