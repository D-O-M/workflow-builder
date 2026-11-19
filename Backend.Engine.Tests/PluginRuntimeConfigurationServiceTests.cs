using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.Engine;
using Backend.Engine.Capabilities;
using Backend.Services.EngineManagement;
using Backend.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Moq;

namespace Backend.Engine.Tests;

public sealed class PluginRuntimeConfigurationServiceTests
{
    [Test]
    public async Task Probe_and_agent_share_only_declared_bindings_and_removing_them_restores_deployment_values()
    {
        const string endpointKey = "Dcc:Gateway:EndpointUrl";
        const string credentialKey = "Dcc:Gateway:ApiKey";
        var workspace = CreatePluginWorkspace();
        var outputRoot = CreateTempDirectory();
        try
        {
            var parser = new PluginManifestParsingUtility(NullLogger<PluginManifestParsingUtility>.Instance);
            var manifest = await parser.ReadAsync(workspace, CancellationToken.None);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [endpointKey] = "https://deployment.example/dcc",
                [credentialKey] = "inherited-credential",
                ["PluginRuntime:ConfigurationFileRoot"] = outputRoot
            }).Build();
            var catalog = new ManagementSettingsCatalog();
            var snapshot = new EnginePolicySnapshot(1, new Dictionary<string, ManagedSetting>
            {
                [endpointKey] = new("https://managed.example/dcc", null),
                [credentialKey] = new(null, "immutable-reference"),
                ["Unrelated:Credential"] = new(null, "another-reference")
            }, new HashSet<int>(), new HashSet<int>());
            var store = new Mock<IManagementSecretStore>(MockBehavior.Strict);
            store.Setup(s => s.ReadAsync("immutable-reference", It.IsAny<CancellationToken>())).ReturnsAsync("rotated-credential");
            var settings = await PluginLaunchConfiguration.ResolveAsync(manifest, configuration, snapshot, () => store.Object,
                CancellationToken.None, catalog);
            var probe = new Mock<IEndpointCapabilityProbe>();
            probe.Setup(p => p.ProbeAsync(new Uri("https://managed.example/dcc"), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EndpointProbeResult(true, null));
            var capability = new PluginRuntimeCapabilityService(parser, Mock.Of<ICapabilityValidationService>(),
                NullLogger<PluginRuntimeCapabilityService>.Instance, Mock.Of<ICommandCapabilityProbe>(), probe.Object);
            (await capability.ValidateAsync(settings, "DCC", CancellationToken.None)).Succeeded.ShouldBeTrue();
            probe.VerifyAll();
            settings.Values["Unrelated:Credential"].ShouldBeNull();
            settings.IsPrivateEngineVariable("Dcc__Gateway__ApiKey").ShouldBeTrue();
            settings.IsPrivateEngineVariable("IDENTITY_HEADER").ShouldBeTrue();
            settings.IsPrivateEngineVariable("PATH").ShouldBeFalse();
            settings.Redact("Plugin logged rotated-credential").ShouldNotContain("rotated-credential");

            using var files = new PluginRuntimeConfigurationService(configuration, parser,
                NullLogger<PluginRuntimeConfigurationService>.Instance);
            var result = await files.WriteFilesAsync(settings, "DCC", CancellationToken.None);
            var json = await File.ReadAllTextAsync(result[0].FullName);
            json.ShouldContain("rotated-credential");
            json.ShouldContain("https://managed.example/dcc");
            var removed = snapshot with { Revision = 2, Settings = new Dictionary<string, ManagedSetting>() };
            settings.NeedsReplacement(removed).ShouldBeTrue();
            var restored = await PluginLaunchConfiguration.ResolveAsync(manifest, configuration, removed, () => store.Object,
                CancellationToken.None, catalog);
            restored.Values[credentialKey].ShouldBe("inherited-credential");
            restored.Values[endpointKey].ShouldBe("https://deployment.example/dcc");
            restored.NeedsReplacement(removed).ShouldBeFalse();
            store.VerifyAll();
            var unavailable = await Should.ThrowAsync<Exception>(() => PluginLaunchConfiguration.ResolveAsync(
                manifest, configuration, snapshot,
                () => throw new IOException("sensitive-provider-diagnostic"), CancellationToken.None, catalog));
            unavailable.Message.ShouldBe("The Plugin credential could not be loaded.");
            unavailable.ToString().ShouldNotContain("sensitive-provider-diagnostic");
        }
        finally
        {
            Directory.Delete(workspace, true);
            Directory.Delete(outputRoot, true);
        }
    }

    [Test]
    public async Task PrepareConfigurationFilesAsync_Writes_private_files_for_each_launch()
    {
        var workspace = CreatePluginWorkspace();
        var outputRoot = CreateTempDirectory();

        try
        {
            using var service = CreateService(
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
            second[0].DirectoryName.ShouldNotBe(first[0].DirectoryName);
            Path.GetFileName(first[0].FullName).ShouldNotContain("secret-value");
            if (OperatingSystem.IsWindows())
            {
                var acl = first[0].Directory!.GetAccessControl();
                acl.AreAccessRulesProtected.ShouldBeTrue();
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                foreach (System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                {
                    rule.IdentityReference.ShouldBe(identity.User);
                }
            }
            else
            {
                File.GetUnixFileMode(first[0].FullName).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var crashed = Directory.CreateDirectory(Path.Combine(outputRoot, $"engine-{Guid.NewGuid():N}"));
            File.WriteAllText(Path.Combine(crashed.FullName, ".workflowbuilder-plugin-config-owner"), "");
            File.WriteAllText(Path.Combine(crashed.FullName, "abandoned.json"), "previous-value");
            using (var restarted = CreateService(outputRoot, []))
            {
                Directory.Exists(crashed.FullName).ShouldBeFalse();
                File.Exists(first[0].FullName).ShouldBeTrue();
            }

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
            using var service = CreateService(outputRoot, []);

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
              "capabilities": {
                "requirements": [{
                  "id": "network:dcc:gateway", "kind": "Network", "capabilityType": "Backend.Engine.Capabilities.DccGatewayCapability",
                  "displayName": "DCC", "endpointConfigKey": "Dcc:Gateway:EndpointUrl",
                  "configurationKeys": ["Dcc:Gateway:EndpointUrl", "Dcc:Gateway:ApiKey"]
                }]
              },
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
