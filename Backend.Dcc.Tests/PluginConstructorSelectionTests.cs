using System;
using System.Reflection;
using Backend.Plugins.Configuration.Dcc;
using Dcc.DccAlertReader;
using Dcc.DeviceAlertReader;
using Dcc.DuisResponseReader;
using Dcc.DuisServiceRequestWriter;
using Dcc.GatewaySubmit;
using Dcc.ParseRequestWriter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Backend.Dcc.Tests;

public sealed class PluginConstructorSelectionTests
{
    [Test]
    [Arguments(typeof(DccAlertReaderPlugin))]
    [Arguments(typeof(DeviceAlertReaderPlugin))]
    [Arguments(typeof(DuisResponseReaderPlugin))]
    [Arguments(typeof(DuisServiceRequestWriterPlugin))]
    [Arguments(typeof(ParseRequestWriterPlugin))]
    public void Harness_factory_creates_blob_plugins_without_test_blob_client_dependencies(Type pluginType)
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var factory = ActivatorUtilities.CreateFactory(pluginType, Type.EmptyTypes);
        var plugin = factory(services, []);

        plugin.ShouldNotBeNull();
        plugin.GetType().ShouldBe(pluginType);
    }

    [Test]
    public void Harness_factory_supplies_gateway_capability_configuration()
    {
        var configuredOptions = new GatewayOptions
        {
            EndpointUrl = new Uri("https://gateway.example.invalid"),
            HttpMethod = "PUT",
            Timeout = TimeSpan.FromSeconds(15)
        };
        using var services = new ServiceCollection()
            .AddSingleton<IOptions<GatewayOptions>>(Options.Create(configuredOptions))
            .BuildServiceProvider();

        var factory = ActivatorUtilities.CreateFactory(typeof(GatewaySubmitPlugin), Type.EmptyTypes);
        var plugin = factory(services, []);
        var field = typeof(GatewaySubmitPlugin).GetField("_gatewayOptions", BindingFlags.Instance | BindingFlags.NonPublic);

        field.ShouldNotBeNull();
        field.GetValue(plugin).ShouldBeSameAs(configuredOptions);
    }
}
