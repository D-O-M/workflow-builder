using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Backend.Engine.Capabilities;
using Backend.Plugins.Configuration;
using Backend.Plugins.Configuration.Dcc;
using Backend.Plugins.Configuration.Tests;
using Backend.Plugins.Harness;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Backend.Plugins.Harness.Tests;

public sealed class PluginConfigurationBinderTests
{
    [Test]
    public void GetConfigPaths_Returns_repeated_config_arguments_in_order()
    {
        var paths = PluginConfigurationArgumentParser
            .GetConfigPaths(["plugin.exe", "--config", "dcc.json", "--config", "cicd.json"])
            .ToArray();

        paths.Select(Path.GetFileName).ShouldBe(["dcc.json", "cicd.json"]);
    }

    [Test]
    public void Bind_Loads_domain_options_from_multiple_config_files()
    {
        var directory = CreateTempDirectory();
        var firstConfigPath = Path.Combine(directory, "dcc-base.json");
        var secondConfigPath = Path.Combine(directory, "dcc-secret.json");
        File.WriteAllText(
            firstConfigPath,
            """
            {
              "Dcc": {
                "Gateway": {
                  "EndpointUrl": "https://localhost/dcc",
                  "Timeout": "00:00:05"
                }
              }
            }
            """);
        File.WriteAllText(
            secondConfigPath,
            """
            {
              "Dcc": {
                "Gateway": {
                  "ApiKey": "top-secret",
                  "HttpMethod": "PUT"
                }
              }
            }
            """);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(firstConfigPath)
                .AddJsonFile(secondConfigPath)
                .Build();

            var options = PluginConfigurationBinder.Bind<GatewayOptions>(configuration);

            options.EndpointUrl.ShouldBe(new Uri("https://localhost/dcc"));
            options.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
            options.HttpMethod.ShouldBe("PUT");
            options.ApiKey.ShouldNotBeNull();
            options.ApiKey.Value.Reveal().ShouldBe("top-secret");
            options.ApiKey.Value.ToString().ShouldBe("********");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void Bind_Allows_missing_section_when_all_values_are_optional()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = PluginConfigurationBinder.Bind<GatewayOptions>(configuration);

        options.EndpointUrl.ShouldBeNull();
        options.ApiKey.ShouldBeNull();
    }

    [Test]
    public void Bind_Throws_when_required_setting_is_missing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new("Tests:Required:OptionalName", "not enough")
            ])
            .Build();

        var ex = Should.Throw<InvalidOperationException>(
            () => PluginConfigurationBinder.Bind<RequiredOptions>(configuration));

        ex.Message.ShouldContain("Tests:Required:RequiredName");
    }

    [Test]
    public async Task HelpWriter_Emits_domain_configuration_keys_and_secret_marker()
    {
        var writer = new StringWriter();

        await PluginConfigurationHelpWriter.WriteAsync(
            writer,
            "DCC gateway submit",
            [typeof(DccGatewayCapability)]);

        var help = writer.ToString();
        help.ShouldContain("plugin --config <domain-config-file> [--config <domain-config-file>]");
        help.ShouldContain("network:dcc:gateway");
        help.ShouldContain("Dcc:Gateway:EndpointUrl");
        help.ShouldContain("Dcc:Gateway:ApiKey");
        help.ShouldContain("optional, secret");
        help.ShouldContain("DCC gateway endpoint");
    }

    [Test]
    public void PluginBuilder_Does_not_expose_direct_configuration_declarations()
    {
        var methods = typeof(PluginBuilder<>)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.Name)
            .ToArray();

        methods.ShouldNotContain("RequireConfiguration");
        methods.ShouldContain("RequireCapability");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "workflow-builder-harness-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
