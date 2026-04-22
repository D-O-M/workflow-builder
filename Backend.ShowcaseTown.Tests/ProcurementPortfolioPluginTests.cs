using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Backend.ShowcaseTown.Tests.TestSupport;
using DeliveryScenarioNormalizer;
using DeliveryWindowAdvisor;
using EcbExchangeRateSnapshot;
using Shouldly;
using WindowWeatherOutlook;

namespace Backend.ShowcaseTown.Tests;

public sealed class ProcurementPortfolioPluginTests
{
    [Test]
    public async Task Delivery_scenario_normalizer_standardizes_codes_and_labels()
    {
        var plugin = new DeliveryScenarioNormalizerPlugin
        {
            Input = new DeliveryScenarioInput
            {
                ShipmentLabel = "May Restock",
                DestinationTown = "Rotterdam",
                DestinationCountryCode = "nl",
                SupplierCountryCode = "de",
                ReceiverCountryCode = "nl",
                BaseCurrencyCode = "usd",
                QuoteCurrencyCode = "eur",
                WindowStartIsoDate = "2026-05-12",
                WindowEndIsoDate = "2026-05-14"
            }
        };

        await plugin.Execute();

        plugin.Scenario.DestinationCountryCode.ShouldBe("NL");
        plugin.Scenario.BaseCurrencyCode.ShouldBe("USD");
        plugin.Scenario.DestinationLabel.ShouldBe("May Restock / Rotterdam");
    }

    [Test]
    public async Task Window_weather_outlook_scores_precipitation_and_wind()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(new
        {
            hourly = new
            {
                temperature_2m = new[] { 12.0, 14.0, 16.0 },
                precipitation = new[] { 0.4, 1.1, 0.0 },
                wind_speed_10m = new[] { 24.0, 30.0, 18.0 }
            }
        })));

        var plugin = new WindowWeatherOutlookPlugin(httpClient)
        {
            Input = new WindowWeatherInput
            {
                LocationLabel = "May Restock / Rotterdam",
                Latitude = 51.9244,
                Longitude = 4.4777,
                WindowStartIsoDate = "2026-05-12",
                WindowEndIsoDate = "2026-05-14"
            }
        };

        await plugin.Execute();

        plugin.Weather.TotalPrecipitationMm.ShouldBe(1.5);
        plugin.Weather.MaxWindSpeedMps.ShouldBeGreaterThan(8.0);
        plugin.Weather.WeatherRiskScore.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Ecb_exchange_rate_snapshot_parses_cross_rate_from_csv()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var csv = request.RequestUri!.AbsoluteUri.Contains("USD", StringComparison.OrdinalIgnoreCase)
                ? "KEY,FREQ,CURRENCY,DENOM,EXR_TYPE,EXR_SUFFIX,TIME_PERIOD,OBS_VALUE\nD.USD.EUR.SP00.A,D,USD,EUR,SP00,A,2026-04-21,1.1767"
                : "KEY,FREQ,CURRENCY,DENOM,EXR_TYPE,EXR_SUFFIX,TIME_PERIOD,OBS_VALUE\nD.GBP.EUR.SP00.A,D,GBP,EUR,SP00,A,2026-04-21,0.87035";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(csv)
            };
        }));

        var plugin = new EcbExchangeRateSnapshotPlugin(httpClient)
        {
            Input = new EcbExchangeRateInput
            {
                BaseCurrencyCode = "USD",
                QuoteCurrencyCode = "GBP"
            }
        };

        await plugin.Execute();

        plugin.Rate.ExchangeRate.ShouldBe(Math.Round(0.87035 / 1.1767, 6));
    }

    [Test]
    public async Task Delivery_window_advisor_applies_thresholds_and_fx_penalty()
    {
        var plugin = new DeliveryWindowAdvisorPlugin
        {
            Input = new DeliveryWindowAdvisorInput
            {
                ShipmentLabel = "May Restock",
                DestinationLabel = "May Restock / Rotterdam",
                BaseCurrencyCode = "USD",
                QuoteCurrencyCode = "EUR",
                WeatherRiskScore = 32,
                WeatherSummary = "Light rain only.",
                AverageDaylightHours = 14.2,
                DaylightSummary = "Long daylight for unloading.",
                SupplierHolidayPressureScore = 20,
                SupplierHolidaySummary = "Supplier calendar is clear.",
                ReceiverHolidayPressureScore = 62,
                ReceiverHolidaySummary = "Receiver has a holiday inside the window.",
                ExchangeRate = 0.93,
                FxSummary = "USD/EUR is near target.",
                TargetExchangeRate = 0.95,
                SensitivityMultiplier = 1.0,
                GoThreshold = 72,
                WatchThreshold = 50
            }
        };

        await plugin.Execute();

        plugin.Decision.DecisionBand.ShouldBe("Proceed");
        plugin.Decision.DecisionScore.ShouldBeGreaterThanOrEqualTo(72);
    }
}
