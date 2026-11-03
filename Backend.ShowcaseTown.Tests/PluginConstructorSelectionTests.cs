using System;
using System.Net.Http;
using System.Reflection;
using AirportAdvisorySnapshot;
using AirportMetarSnapshot;
using AirportStationLookup;
using AirportTafOutlook;
using AirportTrafficSnapshot;
using EcbExchangeRateSnapshot;
using Microsoft.Extensions.DependencyInjection;
using PublicAirQualityOutlook;
using PublicDaylightWindow;
using PublicHolidayWindow;
using Shouldly;
using WindowWeatherOutlook;

namespace Backend.ShowcaseTown.Tests;

public sealed class PluginConstructorSelectionTests
{
    [Test]
    [Arguments(typeof(AirportAdvisorySnapshotPlugin))]
    [Arguments(typeof(AirportMetarSnapshotPlugin))]
    [Arguments(typeof(AirportStationLookupPlugin))]
    [Arguments(typeof(AirportTafOutlookPlugin))]
    [Arguments(typeof(AirportTrafficSnapshotPlugin))]
    [Arguments(typeof(EcbExchangeRateSnapshotPlugin))]
    [Arguments(typeof(PublicAirQualityOutlookPlugin))]
    [Arguments(typeof(PublicDaylightWindowPlugin))]
    [Arguments(typeof(PublicHolidayWindowPlugin))]
    [Arguments(typeof(WindowWeatherOutlookPlugin))]
    public void Harness_factory_uses_the_configured_production_http_client(Type pluginType)
    {
        using var injectedClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var services = new ServiceCollection()
            .AddSingleton(injectedClient)
            .BuildServiceProvider();

        var factory = ActivatorUtilities.CreateFactory(pluginType, Type.EmptyTypes);
        var plugin = factory(services, []);
        var field = pluginType.GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
        field.ShouldNotBeNull();
        using var productionClient = field.GetValue(plugin).ShouldBeOfType<HttpClient>();

        productionClient.ShouldNotBeSameAs(injectedClient);
        productionClient.Timeout.ShouldBe(TimeSpan.FromSeconds(20));
        if (pluginType != typeof(AirportTrafficSnapshotPlugin))
        {
            productionClient.DefaultRequestHeaders.UserAgent.ToString().ShouldContain("workflow-builder/1.0");
        }
    }
}
