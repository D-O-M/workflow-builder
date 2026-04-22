using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Shouldly;
using Backend.ShowcaseTown.Tests.TestSupport;
using MunicipalReadinessAdvisor;
using PublicAirQualityOutlook;
using PublicDaylightWindow;
using PublicHolidayWindow;

namespace Backend.ShowcaseTown.Tests;

public sealed class MunicipalPortfolioPluginTests
{
    [Test]
    public async Task Public_holiday_window_counts_holidays_inside_window()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(new[]
        {
            new { date = "2026-05-25", localName = "Spring Bank Holiday", name = "Spring Bank Holiday" },
            new { date = "2026-05-30", localName = "Too Late", name = "Too Late" }
        })));

        var plugin = new PublicHolidayWindowPlugin(httpClient)
        {
            Input = new HolidayWindowInput
            {
                LocationLabel = "Leeds Centre",
                CountryCode = "gb",
                WindowStartIsoDate = "2026-05-24",
                WindowEndIsoDate = "2026-05-26"
            }
        };

        await plugin.Execute();

        plugin.WindowHoliday.ShouldNotBeNull();
        plugin.WindowHoliday.CountryCode.ShouldBe("GB");
        plugin.WindowHoliday.HolidayCount.ShouldBe(1);
        plugin.WindowHoliday.HeadlineHoliday.ShouldBe("Spring Bank Holiday");
        plugin.WindowHoliday.HolidayPressureScore.ShouldBe(62);
    }

    [Test]
    public async Task Public_daylight_window_averages_day_length_and_scores_it()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            FakeHttpMessageHandler.Json(new { results = new { day_length = 36000 }, status = "OK" }),
            FakeHttpMessageHandler.Json(new { results = new { day_length = 43200 }, status = "OK" })
        ]);

        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => responses.Dequeue()));
        var plugin = new PublicDaylightWindowPlugin(httpClient)
        {
            Input = new DaylightWindowInput
            {
                LocationLabel = "Leeds Centre",
                Latitude = 53.7997,
                Longitude = -1.5492,
                WindowStartIsoDate = "2026-05-24",
                WindowEndIsoDate = "2026-05-25"
            }
        };

        await plugin.Execute();

        plugin.Daylight.ShouldNotBeNull();
        plugin.Daylight.AverageDaylightHours.ShouldBe(11.0);
        plugin.Daylight.DaylightScore.ShouldBe(50);
    }

    [Test]
    public async Task Public_air_quality_outlook_ignores_null_points_and_scores_average()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(new
        {
            hourly = new
            {
                european_aqi = new double?[] { 22, 28, null, 40 }
            }
        })));

        var plugin = new PublicAirQualityOutlookPlugin(httpClient)
        {
            Input = new AirQualityWindowInput
            {
                LocationLabel = "Leeds Centre",
                Latitude = 53.7997,
                Longitude = -1.5492,
                WindowStartIsoDate = "2026-05-24",
                WindowEndIsoDate = "2026-05-25"
            }
        };

        await plugin.Execute();

        plugin.AirQuality.ShouldNotBeNull();
        plugin.AirQuality.AverageAirQualityIndex.ShouldBe(30.0);
        plugin.AirQuality.AirQualityScore.ShouldBe(70);
    }

    [Test]
    public async Task Municipal_readiness_advisor_combines_branch_scores_with_thresholds()
    {
        var plugin = new MunicipalReadinessAdvisorPlugin
        {
            Input = new MunicipalReadinessInput
            {
                LocationLabel = "Leeds Centre",
                WeatherSuitabilityScore = 88,
                WeatherSummary = "Bright and dry.",
                HolidayPressureScore = 62,
                HolidaySummary = "Bank holiday traffic expected.",
                DaylightScore = 74,
                DaylightSummary = "Long daylight window.",
                AirQualityScore = 80,
                AirQualitySummary = "Clean enough for outdoor footfall.",
                SensitivityMultiplier = 1.05,
                GoThreshold = 78,
                WatchThreshold = 55
            }
        };

        await plugin.Execute();

        plugin.Recommendation.ShouldNotBeNull();
        plugin.Recommendation.ReadinessScore.ShouldBeGreaterThan(78);
        plugin.Recommendation.RecommendationBand.ShouldBe("ScaleUp");
    }
}
