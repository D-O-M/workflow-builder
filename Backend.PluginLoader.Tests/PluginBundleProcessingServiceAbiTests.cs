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

public sealed class PluginBundleProcessingServiceAbiTests
{
    [Test]
    public async Task ProcessAsync_Registers_compatible_abi_metadata()
    {
        var manifest = MinimalManifest(PluginAbiDefaults.CurrentMetadata);

        var (capturedVersion, _) = await ProcessManifestAsync(manifest);

        capturedVersion.ShouldNotBeNull();
        capturedVersion.Abi.Version.ShouldBe(PluginAbiDefaults.CurrentVersion);
        capturedVersion.Abi.InvocationEnvelopeSchemaVersion.ShouldBe(PluginAbiDefaults.CurrentInvocationEnvelopeSchemaVersion);
        capturedVersion.Abi.ProcessProtocolVersion.ShouldBe(PluginAbiDefaults.CurrentProcessProtocolVersion);
        capturedVersion.Abi.SupportedStateStoreModes.ShouldBe(PluginAbiDefaults.CurrentSupportedStateStoreModes);
        capturedVersion.Abi.SupportedPropertyKinds.ShouldBe(PluginAbiDefaults.CurrentSupportedPropertyKinds);
    }

    [Test]
    public async Task ProcessAsync_Rejects_missing_abi_metadata()
    {
        var manifest = MinimalManifest(abi: null);

        var ex = await Should.ThrowAsync<PluginAbiCompatibilityException>(
            () => ProcessManifestAsync(manifest));

        ex.Result.Status.ShouldBe(PluginAbiCompatibilityStatus.MissingMetadata);
        ex.Message.ShouldContain("ABI_MISSING");
    }

    [Test]
    public async Task ProcessAsync_Rejects_unsupported_abi_version()
    {
        var manifest = MinimalManifest(
            PluginAbiDefaults.CurrentMetadata with
            {
                Version = new PluginAbiVersion(1, 1),
                ResultSchemaVersion = 1,
                HydrationProtocolVersion = 1,
                HarnessVersion = "1.0.0"
            });

        var ex = await Should.ThrowAsync<PluginAbiCompatibilityException>(
            () => ProcessManifestAsync(manifest));

        ex.Result.Status.ShouldBe(PluginAbiCompatibilityStatus.Incompatible);
        ex.Message.ShouldContain("ABI_VERSION_UNSUPPORTED");
    }

    [Test]
    public async Task ProcessAsync_Rejects_legacy_process_protocol_metadata()
    {
        var manifest = MinimalManifest(
            PluginAbiDefaults.CurrentMetadata with
            {
                ProcessProtocolVersion = 0
            });

        var ex = await Should.ThrowAsync<PluginAbiCompatibilityException>(
            () => ProcessManifestAsync(manifest));

        ex.Result.Status.ShouldBe(PluginAbiCompatibilityStatus.Incompatible);
        ex.Message.ShouldContain("PROCESS_PROTOCOL_UNSUPPORTED");
    }

    [Test]
    public async Task ProcessAsync_Rejects_unsupported_state_store_mode()
    {
        var manifest = MinimalManifest(
            PluginAbiDefaults.CurrentMetadata with
            {
                SupportedStateStoreModes = ["Cosmos", "Sql"]
            });

        var ex = await Should.ThrowAsync<PluginAbiCompatibilityException>(
            () => ProcessManifestAsync(manifest));

        ex.Message.ShouldContain("STATE_STORE_MODE_UNSUPPORTED");
    }

    [Test]
    public async Task ProcessAsync_Rejects_property_kind_not_covered_by_abi()
    {
        var manifest = MinimalManifest(
            PluginAbiDefaults.CurrentMetadata with
            {
                SupportedPropertyKinds = ["String"]
            });
        manifest.OutputChannels.Add(new ManifestFileChannelInfo
        {
            Name = "Success",
            Type = "AbiPlugin.Success",
            Properties =
            [
                new ManifestFilePropertyInfo
                {
                    Name = "Score",
                    Kind = "Integer",
                    Required = true
                }
            ]
        });

        var ex = await Should.ThrowAsync<PluginAbiCompatibilityException>(
            () => ProcessManifestAsync(manifest));

        ex.Message.ShouldContain("DECLARED_PROPERTY_KIND_NOT_COVERED");
    }

    private static async Task<(TaskVersionDomainModel? CapturedVersion, Mock<ITaskDefinitionRepository> Repository)> ProcessManifestAsync(
        ManifestFile manifest)
    {
        var workspaceDirectory = Path.Combine(Path.GetTempPath(), "workflow-builder-plugin-abi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspaceDirectory, "app"));
        File.WriteAllText(Path.Combine(workspaceDirectory, "app", "AbiPlugin.dll"), string.Empty);

        try
        {
            TaskVersionDomainModel? capturedVersion = null;
            var pluginInfo = new PluginBlobInfo("AbiPlugin_0.1.0.zip", "AbiPlugin", new Version(0, 1, 0));

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
                services.GetRequiredService<IServiceScopeFactory>());

            await service.ProcessAsync(pluginInfo, CancellationToken.None);
            return (capturedVersion, repository);
        }
        finally
        {
            Directory.Delete(workspaceDirectory, recursive: true);
        }
    }

    private static PluginBundleProcessingService CreateService(
        string workspaceDirectory,
        ManifestFile manifest,
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
            .ReturnsAsync(EmptySchemas());

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

    private static ManifestFile MinimalManifest(PluginAbiMetadata? abi) =>
        new()
        {
            DisplayName = "ABI plugin",
            Abi = abi,
            OutputChannels = []
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
