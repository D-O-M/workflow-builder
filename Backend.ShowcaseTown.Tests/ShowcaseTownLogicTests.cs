using System;
using Shouldly;
using ShowcaseTownForecast;
using ShowcaseTownHoliday;
using ShowcaseTownRecommendation;
using HolidayTownWeekendForecast = ShowcaseTownHoliday.TownWeekendForecast;

namespace Backend.ShowcaseTown.Tests;

public sealed class ShowcaseTownLogicTests
{
    [Test]
    public void Weather_scoring_rewards_mild_dry_calm_conditions()
    {
        var strongWeekend = ShowcaseTownWeatherScoring.CalculateWeatherSuitabilityScore(
            averageTemperatureC: 18,
            totalPrecipitationMm: 0,
            maximumWindSpeedMps: 4);
        var roughWeekend = ShowcaseTownWeatherScoring.CalculateWeatherSuitabilityScore(
            averageTemperatureC: 4,
            totalPrecipitationMm: 6,
            maximumWindSpeedMps: 12);

        strongWeekend.ShouldBe(100);
        roughWeekend.ShouldBe(42);
        roughWeekend.ShouldBeLessThan(strongWeekend);
    }

    [Test]
    public void Recommendation_scoring_clamps_and_maps_band_thresholds()
    {
        ShowcaseTownRecommendationScoring.CalculateFinalRecommendationScore(80, 0.15).ShouldBe(92);
        ShowcaseTownRecommendationScoring.CalculateFinalRecommendationScore(90, 0.5).ShouldBe(100);

        ShowcaseTownRecommendationScoring.GetRecommendationBand(75).ShouldBe("Go");
        ShowcaseTownRecommendationScoring.GetRecommendationBand(74).ShouldBe("Maybe");
        ShowcaseTownRecommendationScoring.GetRecommendationBand(49).ShouldBe("Skip");
    }

    [Test]
    public void Holiday_fallback_preserves_forecast_and_zeroes_adjustment()
    {
        var input = new HolidayTownWeekendForecast
        {
            RequestedTownName = "Porto",
            ResolvedTownName = "Porto",
            ResolvedDisplayName = "Porto, Porto, Portugal",
            ResolvedCountryCode = "PT",
            Latitude = 41.1496,
            Longitude = -8.6109,
            WeekendStartIsoDate = new DateOnly(2026, 4, 25),
            WeekendEndIsoDate = new DateOnly(2026, 4, 26),
            AverageTemperatureC = 19.5,
            MaximumWindSpeedMps = 5.2,
            TotalPrecipitationMm = 0.8,
            RainRisk01 = 0.08,
            WeatherSuitabilityScore = 93,
            WeatherSummary = "Pleasant and dry."
        };

        var fallback = ShowcaseTownHolidayFallback.CreateUnavailableContext(
            input,
            "Holiday lookup was unavailable.");

        fallback.ResolvedTownName.ShouldBe(input.ResolvedTownName);
        fallback.WeatherSuitabilityScore.ShouldBe(input.WeatherSuitabilityScore);
        fallback.HolidayLookupSucceeded.ShouldBeFalse();
        fallback.NearbyHolidayDetected.ShouldBeFalse();
        fallback.HolidayDemandAdjustmentPct.ShouldBe(0);
        fallback.HolidaySummary.ShouldBe("Holiday lookup was unavailable.");
    }
}
