using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine;
using Backend.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Backend.Engine.Tests;

public sealed class PluginRuntimeConfigurationServiceTests
{
    [Test]
    public async Task PrepareConfigurationFilesAsync_Writes_domain_file_and_reuses_matching_values()
    {
        var workspace = CreatePluginWorkspace();
        var outputRoot = CreateTempDirectory();

        try
        {
            var service = CreateService(
                outputRoot,
                [
                    new("Dcc:Gateway:EndpointUrl", "https://localhost/dcc"),
                    new("Dcc:Gateway:ApiKey", "secret-value")
                ]);

            var first = await service.PrepareConfigurationFilesAsync(
                new FileInfo(Path.Combine(workspace, "app", "plugin.dll")),
                "DCC gateway submit",
                CancellationToken.None);
            var second = await service.PrepareConfigurationFilesAsync(
                new FileInfo(Path.Combine(workspace, "app", "plugin.dll")),
                "DCC gateway submit",
                CancellationToken.None);

            first.Count.ShouldBe(1);
            second.Count.ShouldBe(1);
            second[0].FullName.ShouldBe(first[0].FullName);
            Path.GetFileName(first[0].FullName).ShouldStartWith("Dcc-");
            Path.GetFileName(first[0].FullName).ShouldNotContain("secret-value");

            var json = await File.ReadAllTextAsync(first[0].FullName);
            json.ShouldContain("\"Dcc\"");
            json.ShouldContain("\"Gateway\"");
            json.ShouldContain("\"EndpointUrl\"");
            json.ShouldContain("\"ApiKey\"");
            json.ShouldContain("secret-value");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Test]
    public async Task PrepareConfigurationFilesAsync_Throws_when_required_value_is_missing()
    {
        var workspace = CreatePluginWorkspace(requiredEndpoint: true);
        var outputRoot = CreateTempDirectory();

        try
        {
            var service = CreateService(outputRoot, []);

            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => service.PrepareConfigurationFilesAsync(
                    new FileInfo(Path.Combine(workspace, "app", "plugin.dll")),
                    "DCC gateway submit",
                    CancellationToken.None));

            ex.Message.ShouldContain("Dcc:Gateway:EndpointUrl");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    private static PluginRuntimeConfigurationService CreateService(
        string outputRoot,
        IReadOnlyList<KeyValuePair<string, string?>> values)
    {
        var configurationValues = new List<KeyValuePair<string, string?>>(values)
        {
            new("PluginRuntime:ConfigurationFileRoot", outputRoot)
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        return new PluginRuntimeConfigurationService(
            configuration,
            new PluginManifestParsingUtility(NullLogger<PluginManifestParsingUtility>.Instance),
            NullLogger<PluginRuntimeConfigurationService>.Instance);
    }

    private static string CreatePluginWorkspace(bool requiredEndpoint = false)
    {
        var workspace = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(workspace, "app"));
        File.WriteAllText(Path.Combine(workspace, "app", "plugin.dll"), "");
        File.WriteAllText(
            Path.Combine(workspace, "manifest.json"),
            $$"""
            {
              "displayName": "DCC gateway submit",
              "inputChannel": null,
              "outputChannels": [],
              "globalState": null,
              "configuration": {
                "requirements": [
                  {
                    "domainId": "Dcc",
                    "optionsType": "Backend.Plugins.Configuration.Dcc.GatewayOptions",
                    "optionsName": "Gateway",
                    "key": "Dcc:Gateway:EndpointUrl",
                    "propertyPath": "EndpointUrl",
                    "clrType": "System.Uri",
                    "kind": "Uri",
                    "required": {{requiredEndpoint.ToString().ToLowerInvariant()}},
                    "secret": false,
                    "isCollection": false
                  },
                  {
                    "domainId": "Dcc",
                    "optionsType": "Backend.Plugins.Configuration.Dcc.GatewayOptions",
                    "optionsName": "Gateway",
                    "key": "Dcc:Gateway:ApiKey",
                    "propertyPath": "ApiKey",
                    "clrType": "string",
                    "kind": "String",
                    "required": false,
                    "secret": true,
                    "isCollection": false
                  }
                ]
              }
            }
            """);
        return workspace;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "workflow-builder-engine-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
