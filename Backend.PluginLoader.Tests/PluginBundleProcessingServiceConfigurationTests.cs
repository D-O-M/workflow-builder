using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Models;
using Backend.PluginAbi;
using Backend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.PluginLoader.Tests;

public sealed class PluginBundleProcessingServiceConfigurationTests
{
    [Test]
    public async Task ProcessAsync_Rejects_configuration_without_capability_owner()
    {
        var manifest = ManifestWithRequirements([ValidRequirement()]);
        manifest.Capabilities.Requirements.Clear();

        var ex = await Should.ThrowAsync<InvalidDataException>(
            () => ProcessManifestAsync(manifest));

        ex.Message.ShouldContain("must be owned by a declared capability");
    }

    [Test]
    public async Task ProcessAsync_Rejects_capability_type_outside_approved_namespace()
    {
        var manifest = ManifestWithRequirements([ValidRequirement()]);
        manifest.Capabilities.Requirements[0].CapabilityType = "Local.GatewayCapability";

        var ex = await Should.ThrowAsync<InvalidDataException>(
            () => ProcessManifestAsync(manifest));

        ex.Message.ShouldContain("outside the approved Backend.Engine.Capabilities namespace");
    }

    [Test]
    public async Task ProcessAsync_Rejects_configuration_source_capability_type_mismatch()
    {
        var manifest = ManifestWithRequirements(
        [
            ValidRequirement() with
            {
                SourceCapabilityType = "Backend.Engine.Capabilities.OtherGatewayCapability"
            }
        ]);

        var ex = await Should.ThrowAsync<InvalidDataException>(
            () => ProcessManifestAsync(manifest));

        ex.Message.ShouldContain("declares source capability type");
    }

    [Test]
    public async Task ProcessAsync_Rejects_configuration_type_outside_approved_domain()
    {
        var manifest = ManifestWithRequirements(
        [
            ValidRequirement() with
            {
                OptionsType = "Local.PluginOptions"
            }
        ]);

        var ex = await Should.ThrowAsync<InvalidDataException>(
            () => ProcessManifestAsync(manifest));

        ex.Message.ShouldContain("outside the approved domain");
    }

    [Test]
    public async Task ProcessAsync_Rejects_conflicting_metadata_for_same_canonical_key()
    {
        var manifest = ManifestWithRequirements(
        [
            ValidRequirement(),
            ValidRequirement() with
            {
                Secret = true
            }
        ]);

        var ex = await Should.ThrowAsync<InvalidDataException>(
            () => ProcessManifestAsync(manifest));

        ex.Message.ShouldContain("conflicting metadata");
        ex.Message.ShouldContain("Dcc:Gateway:EndpointUrl");
    }

    [Test]
    public async Task ProcessAsync_Rejects_short_alias_collision_for_different_keys()
    {
        var manifest = ManifestWithRequirements(
        [
            ValidRequirement(),
            ValidRequirement() with
            {
                OptionsName = "MockGateway",
                Key = "Dcc:MockGateway:EndpointUrl"
            }
        ]);

        var ex = await Should.ThrowAsync<InvalidDataException>(
            () => ProcessManifestAsync(manifest));

        ex.Message.ShouldContain("short configuration alias 'EndpointUrl'");
    }

    private static async Task ProcessManifestAsync(ManifestFile manifest)
    {
        var workspaceDirectory = Path.Combine(Path.GetTempPath(), "workflow-builder-plugin-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspaceDirectory, "app"));
        File.WriteAllText(Path.Combine(workspaceDirectory, "app", "ConfigPlugin.dll"), string.Empty);

        try
        {
            var service = CreateService(workspaceDirectory, manifest);
            await service.ProcessAsync(
                new PluginBlobInfo("ConfigPlugin_0.1.0.zip", "ConfigPlugin", new Version(0, 1, 0)),
                CancellationToken.None);
        }
        finally
        {
            Directory.Delete(workspaceDirectory, recursive: true);
        }
    }

    private static PluginBundleProcessingService CreateService(
        string workspaceDirectory,
        ManifestFile manifest)
    {
        var bundleSource = new Mock<IPluginBundleSource>();
        bundleSource
            .Setup(candidate => candidate.ExtractBundleAsync(It.IsAny<PluginBlobInfo>(), workspaceDirectory, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var workspaceFactory = new Mock<IPluginWorkspaceFactoryUtility>();
        workspaceFactory
            .Setup(candidate => candidate.CreateWorkspaceAsync(It.IsAny<string>(), It.IsAny<Version>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaceDirectory);

        var manifestParser = new Mock<IPluginManifestParsingUtility>();
        manifestParser
            .Setup(candidate => candidate.ReadAsync(workspaceDirectory, It.IsAny<CancellationToken>()))
            .ReturnsAsync(manifest);

        var schemaAggregator = new Mock<IPluginSchemaAggregationUtility>();
        schemaAggregator
            .Setup(candidate => candidate.LoadAsync(workspaceDirectory, It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySchemas());

        var reloadQueue = new Mock<IPluginReloadQueueWriter>();
        reloadQueue
            .Setup(candidate => candidate.EnqueueAsync(It.IsAny<Backend.Plugins.Communication.PluginReloadRequestMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var repository = new Mock<ITaskDefinitionRepository>();
        repository
            .Setup(candidate => candidate.GetAll(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var services = new ServiceCollection()
            .AddSingleton(repository.Object)
            .BuildServiceProvider();

        return new PluginBundleProcessingService(
            bundleSource.Object,
            workspaceFactory.Object,
            manifestParser.Object,
            schemaAggregator.Object,
            reloadQueue.Object,
            new PluginAbiCompatibilityService(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PluginBundleProcessingService>.Instance);
    }

    private static ManifestFile ManifestWithRequirements(
        List<ManifestFileConfigurationRequirementInfo> requirements) =>
        new()
        {
            DisplayName = "Config plugin",
            Abi = PluginAbiDefaults.CurrentMetadata,
            OutputChannels = [],
            Capabilities = new ManifestFileCapabilitiesInfo
            {
                Requirements =
                [
                    ValidCapability()
                ]
            },
            Configuration = new ManifestFileConfigurationInfo
            {
                Requirements = requirements
            }
        };

    private static ManifestFileCapabilityRequirementInfo ValidCapability() =>
        new()
        {
            Id = "network:dcc:gateway",
            Kind = "Network",
            CapabilityType = "Backend.Engine.Capabilities.DccGatewayCapability",
            DisplayName = "DCC gateway access",
            Required = true,
            ConfigurationKeys =
            [
                "Dcc:Gateway:EndpointUrl",
                "Dcc:MockGateway:EndpointUrl"
            ]
        };

    private static ManifestFileConfigurationRequirementInfo ValidRequirement() =>
        new()
        {
            SourceCapabilityId = "network:dcc:gateway",
            SourceCapabilityType = "Backend.Engine.Capabilities.DccGatewayCapability",
            DomainId = "Dcc",
            OptionsType = "Backend.Plugins.Configuration.Dcc.GatewayOptions",
            OptionsName = "Gateway",
            Key = "Dcc:Gateway:EndpointUrl",
            PropertyPath = "EndpointUrl",
            ClrType = "System.Uri",
            Kind = "Uri",
            Required = false,
            Secret = false,
            Description = "DCC gateway endpoint."
        };

    private static PluginSchemaResult EmptySchemas() =>
        new(
            JsonNode.Parse("""{"components":{"schemas":{}}}""")!,
            new SchemasFile
            {
                Components = new SchemasFileComponents
                {
                    Schemas = new Dictionary<string, TypeSchema>(StringComparer.Ordinal)
                }
            },
            DefinitionCount: 0);
}
