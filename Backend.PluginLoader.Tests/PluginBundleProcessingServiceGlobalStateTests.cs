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
using Backend.Models.DomainModels;
using Backend.PluginAbi;
using Backend.Services;
using Backend.Services.PluginIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Backend.PluginLoader.Tests;

public sealed class PluginBundleProcessingServiceGlobalStateTests
{
    [Test]
    public async Task ProcessAsync_RegistersNoGlobalStateDefinition_WhenManifestGlobalStateIsNull()
    {
        var capturedVersion = await ProcessManifestAsync(new ManifestFile
        {
            DisplayName = "No state plugin",
            Abi = PluginAbiDefaults.CurrentMetadata,
            GlobalState = null
        });

        capturedVersion.RequiredGlobalState.ShouldBeNull();
    }

    [Test]
    public async Task ProcessAsync_TreatsLegacyGlobalStateStubManifestAsNoGlobalStateDefinition()
    {
        var capturedVersion = await ProcessManifestAsync(
            new ManifestFile
            {
                DisplayName = "Legacy no state plugin",
                Abi = PluginAbiDefaults.CurrentMetadata,
                GlobalState = new ManifestFileChannelInfo
                {
                    Name = "GlobalState",
                    Type = "Backend.Plugins.Harness.GlobalStateStub"
                }
            },
            new PluginSchemaResult(
                JsonNode.Parse("""{"components":{"schemas":{}}}""")!,
                new SchemasFile
                {
                    Components = new SchemasFileComponents
                    {
                        Schemas = new Dictionary<string, TypeSchema>(StringComparer.Ordinal)
                        {
                            ["Backend.Plugins.Harness.GlobalStateStub"] = new()
                            {
                                Type = ["object"],
                                Required = ["Empty"],
                                Properties = new Dictionary<string, Backend.Models.TypeInfo>(StringComparer.Ordinal)
                                {
                                    ["Empty"] = new()
                                    {
                                        Type = ["boolean"]
                                    }
                                }
                            }
                        }
                    }
                },
                DefinitionCount: 1));

        capturedVersion.RequiredGlobalState.ShouldBeNull();
    }

    private static async Task<TaskVersionDomainModel> ProcessManifestAsync(
        ManifestFile manifest,
        PluginSchemaResult? schemas = null)
    {
        var workspaceDirectory = Path.Combine(Path.GetTempPath(), "workflow-builder-plugin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspaceDirectory, "app"));
        File.WriteAllText(Path.Combine(workspaceDirectory, "app", "NoStatePlugin.dll"), string.Empty);

        try
        {
            TaskVersionDomainModel? capturedVersion = null;
            var pluginInfo = new PluginBlobInfo("NoStatePlugin_0.1.0.zip", "NoStatePlugin", new Version(0, 1, 0));

            var repository = new Mock<ITaskDefinitionRepository>();
            repository
                .Setup(candidate => candidate.GetAll(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            repository
                .Setup(candidate => candidate.CreateVersion(It.IsAny<TaskVersionDomainModel>(), It.IsAny<CancellationToken>()))
                .Callback<TaskVersionDomainModel, CancellationToken>((version, _) => capturedVersion = version)
                .ReturnsAsync(() => new TaskVersionRegistrationResult(
                    TaskVersionId: 1,
                    TaskDefinitionId: 2,
                    TaskName: pluginInfo.PluginName,
                    Version: pluginInfo.Version,
                    ChangeKind: PluginVersionChangeKind.Initial));

            var services = new ServiceCollection()
                .AddSingleton(repository.Object)
                .BuildServiceProvider();

            var service = CreateService(
                workspaceDirectory,
                manifest,
                schemas ?? EmptySchemas(),
                services.GetRequiredService<IServiceScopeFactory>());

            await service.ProcessAsync(pluginInfo, CancellationToken.None);

            capturedVersion.ShouldNotBeNull();
            return capturedVersion;
        }
        finally
        {
            Directory.Delete(workspaceDirectory, recursive: true);
        }
    }

    private static PluginBundleProcessingService CreateService(
        string workspaceDirectory,
        ManifestFile manifest,
        PluginSchemaResult schemas,
        IServiceScopeFactory scopeFactory)
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
            .ReturnsAsync(schemas);

        var reloadQueue = new Mock<IPluginReloadQueueWriter>();
        reloadQueue
            .Setup(candidate => candidate.EnqueueAsync(It.IsAny<Backend.Plugins.Communication.PluginReloadRequestMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new PluginBundleProcessingService(
            bundleSource.Object,
            workspaceFactory.Object,
            manifestParser.Object,
            schemaAggregator.Object,
            reloadQueue.Object,
            new InMemoryPluginIngestionStatusStore(),
            new PluginAbiCompatibilityService(),
            scopeFactory,
            NullLogger<PluginBundleProcessingService>.Instance);
    }

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
