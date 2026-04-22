using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AirportAdvisorySnapshot;
using AirportDisruptionAdvisor;
using AirportMetarSnapshot;
using AirportOperationWindow;
using AirportStationLookup;
using AirportTafOutlook;
using AirportTrafficSnapshot;
using AirportWeatherRisk;
using Backend.ShowcaseTown.Tests.TestSupport;
using Shouldly;

namespace Backend.ShowcaseTown.Tests;

public sealed class AviationPortfolioPluginTests
{
    [Test]
    public async Task Airport_operation_window_normalizes_code_and_label()
    {
        var plugin = new AirportOperationWindowPlugin
        {
            Input = new AirportOperationWindowInput
            {
                AirportCode = "kjfk",
                WindowStartIsoDate = "2026-05-12",
                WindowEndIsoDate = "2026-05-12"
            }
        };

        await plugin.Execute();

        plugin.Window.AirportCode.ShouldBe("KJFK");
        plugin.Window.WindowLabel.ShouldBe("2026-05-12 to 2026-05-12");
    }

    [Test]
    public async Task Airport_station_lookup_parses_faa_station_info()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(new[]
        {
            new { icaoId = "KJFK", site = "New York/JF Kennedy Intl", lat = 40.63916, lon = -73.76394, country = "US" }
        })));

        var plugin = new AirportStationLookupPlugin(httpClient)
        {
            Input = new AirportStationLookupInput
            {
                AirportCode = "KJFK"
            }
        };

        await plugin.Execute();

        plugin.Station.StationCode.ShouldBe("KJFK");
        plugin.Station.AirportName.ShouldBe("New York/JF Kennedy Intl");
        plugin.Station.CountryCode.ShouldBe("US");
    }

    [Test]
    public async Task Airport_metar_snapshot_extracts_flight_category_and_ceiling()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(new[]
        {
            new
            {
                rawOb = "METAR TEST",
                fltCat = "MVFR",
                wspd = 17,
                visib = "4",
                clouds = new[]
                {
                    new { cover = "OVC", @base = 1800 }
                }
            }
        })));

        var plugin = new AirportMetarSnapshotPlugin(httpClient)
        {
            Input = new AirportMetarInput
            {
                StationCode = "KJFK"
            }
        };

        await plugin.Execute();

        plugin.Metar.FlightCategory.ShouldBe("MVFR");
        plugin.Metar.CeilingFeet.ShouldBe(1800);
        plugin.Metar.VisibilityMiles.ShouldBe(4);
    }

    [Test]
    public async Task Airport_taf_outlook_finds_worst_overlapping_segment()
    {
        var windowStart = new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(new[]
        {
            new
            {
                rawTAF = "TAF TEST",
                fcsts = new object[]
                {
                    new
                    {
                        timeFrom = windowStart,
                        timeTo = windowStart + 7200,
                        wspd = 12,
                        visib = "6+",
                        clouds = new[]
                        {
                            new { cover = "BKN", @base = 4000 }
                        }
                    },
                    new
                    {
                        timeFrom = windowStart + 7200,
                        timeTo = windowStart + 14400,
                        wspd = 22,
                        visib = 2,
                        clouds = new[]
                        {
                            new { cover = "OVC", @base = 700 }
                        }
                    }
                }
            }
        })));

        var plugin = new AirportTafOutlookPlugin(httpClient)
        {
            Input = new AirportTafInput
            {
                StationCode = "KJFK",
                WindowStartIsoDate = "2026-05-04",
                WindowEndIsoDate = "2026-05-04"
            }
        };

        await plugin.Execute();

        plugin.Taf.WorstFlightCategory.ShouldBe("IFR");
        plugin.Taf.ForecastCeilingFeet.ShouldBe(700);
        plugin.Taf.ForecastWindSpeedKnots.ShouldBe(22);
    }

    [Test]
    public async Task Airport_advisory_snapshot_counts_nearby_sigmets()
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(new object[]
        {
            new
            {
                severity = 4,
                coords = new[]
                {
                    new { lat = 40.8, lon = -73.9 }
                }
            },
            new
            {
                severity = 2,
                coords = new[]
                {
                    new { lat = 30.0, lon = -120.0 }
                }
            }
        })));

        var plugin = new AirportAdvisorySnapshotPlugin(httpClient)
        {
            Input = new AirportAdvisoryInput
            {
                AirportCode = "KJFK",
                Latitude = 40.63916,
                Longitude = -73.76394
            }
        };

        await plugin.Execute();

        plugin.Advisory.AdvisoryCount.ShouldBe(1);
        plugin.Advisory.HighestAdvisorySeverityScore.ShouldBe(4);
    }

    [Test]
    public async Task Airport_traffic_snapshot_counts_total_and_ground_aircraft()
    {
        var json = "{\"states\":[[\"abc\",\"CALL\",\"US\",0,0,-73.8,40.6,0,true,0,0,0,null,0,null,false,0],[\"def\",\"CALL2\",\"US\",0,0,-73.7,40.7,1000,false,0,0,0,null,0,null,false,0]]}";
        var httpClient = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        }));

        var plugin = new AirportTrafficSnapshotPlugin(httpClient)
        {
            Input = new AirportTrafficInput
            {
                AirportCode = "KJFK",
                Latitude = 40.63916,
                Longitude = -73.76394
            }
        };

        await plugin.Execute();

        plugin.Traffic.NearbyAircraftCount.ShouldBe(2);
        plugin.Traffic.GroundAircraftCount.ShouldBe(1);
        plugin.Traffic.TrafficPressureScore.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task Airport_weather_risk_penalizes_ifr_and_advisories()
    {
        var plugin = new AirportWeatherRiskPlugin
        {
            Input = new AirportWeatherRiskInput
            {
                AirportCode = "KJFK",
                MetarFlightCategory = "MVFR",
                MetarWindSpeedKnots = 18,
                MetarVisibilityMiles = 4,
                MetarCeilingFeet = 1800,
                TafWorstFlightCategory = "IFR",
                TafForecastWindSpeedKnots = 28,
                TafForecastVisibilityMiles = 2,
                TafForecastCeilingFeet = 700,
                AdvisoryCount = 2,
                HighestAdvisorySeverityScore = 4
            }
        };

        await plugin.Execute();

        plugin.Risk.OperationalWeatherRiskScore.ShouldBeGreaterThan(50);
    }

    [Test]
    public async Task Airport_disruption_advisor_escalates_when_weather_and_traffic_stack()
    {
        var plugin = new AirportDisruptionAdvisorPlugin
        {
            Input = new AirportDisruptionAdvisorInput
            {
                AirportCode = "KJFK",
                AirportName = "New York/JF Kennedy Intl",
                WindowLabel = "2026-05-12 to 2026-05-12",
                OperationalWeatherRiskScore = 86,
                WeatherSummary = "IFR expected with advisory pressure.",
                NearbyAircraftCount = 88,
                GroundAircraftCount = 30,
                TrafficPressureScore = 92,
                AverageDaylightHours = 13.5,
                DaylightSummary = "Long daylight reduces some operational drag.",
                SensitivityMultiplier = 1.1,
                EscalateThreshold = 70,
                MonitorThreshold = 45
            }
        };

        await plugin.Execute();

        plugin.Advisory.AdvisoryBand.ShouldBe("Escalate");
        plugin.Advisory.DisruptionScore.ShouldBeGreaterThanOrEqualTo(70);
    }
}
