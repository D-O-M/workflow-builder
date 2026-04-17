using Backend.Plugins.Harness;

namespace ShowcaseTownRecommendation;

public sealed class ShowcaseTownRecommendationPlugin : IPlugin
{
    public required TownWeekendHolidayContext TownWeekendHolidayContext { get; init; }
    public TownRecommendation TownRecommendation { get; set; } = new();

    public Task Execute()
    {
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
                $"{TownWeekendHolidayContext.ResolvedTownName} is a {recommendationBand} for {TownWeekendHolidayContext.WeekendStartIsoDate} to {TownWeekendHolidayContext.WeekendEndIsoDate}. Final score {finalRecommendationScore}/100.",
            WeatherSummary = TownWeekendHolidayContext.WeatherSummary,
            HolidaySummary = TownWeekendHolidayContext.HolidaySummary
        };

        return Task.CompletedTask;
    }
}
