using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
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

public sealed class PluginBundleProcessingServiceIngestionStatusTests
{
    [Test]
    public async Task ProcessAsync_missing_manifest_records_manifest_missing_and_quarantine()
    {
        using var harness = PluginProcessingHarness.Create(manifest: null);

        await harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None);

        var record = await harness.GetStatusRecordAsync();
        record.ShouldNotBeNull();
        record.CurrentStatus.ShouldBe(PluginIngestionStatus.Quarantined);
        record.Quarantined.ShouldBeTrue();
        record.Events.ShouldContain(statusEvent => statusEvent.Status == PluginIngestionStatus.ManifestMissing);
        record.Events.ShouldContain(statusEvent => statusEvent.Status == PluginIngestionStatus.Quarantined);
    }

    [Test]
    public async Task ProcessAsync_invalid_abi_records_abi_invalid_and_quarantine()
    {
        using var harness = PluginProcessingHarness.Create(MinimalManifest(abi: null));

        await Should.ThrowAsync<PluginAbiCompatibilityException>(() =>
            harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None));

        var record = await harness.GetStatusRecordAsync();
        record.ShouldNotBeNull();
        record.CurrentStatus.ShouldBe(PluginIngestionStatus.Quarantined);
        record.LastFailureCategory.ShouldBe(PluginIngestionStatus.AbiInvalid);
    }

    [Test]
    public async Task ProcessAsync_missing_schemas_records_schema_invalid_and_quarantine()
    {
        using var harness = PluginProcessingHarness.Create(
            MinimalManifest(PluginAbiDefaults.CurrentMetadata),
            missingSchemas: true);

        await Should.ThrowAsync<InvalidDataException>(() =>
            harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None));

        var record = await harness.GetStatusRecordAsync();
        record.ShouldNotBeNull();
        record.Quarantined.ShouldBeTrue();
        record.LastFailureCategory.ShouldBe(PluginIngestionStatus.SchemaInvalid);
    }

    [Test]
    public async Task ProcessAsync_invalid_capability_metadata_records_capability_status()
    {
        var manifest = MinimalManifest(PluginAbiDefaults.CurrentMetadata);
        manifest.Capabilities.Requirements.Add(new ManifestFileCapabilityRequirementInfo
        {
            Id = "network:test",
            Kind = "Network",
            CapabilityType = "Local.PluginCapability",
            DisplayName = "Local capability"
        });
        using var harness = PluginProcessingHarness.Create(manifest);

        await Should.ThrowAsync<InvalidDataException>(() =>
            harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None));

        var record = await harness.GetStatusRecordAsync();
        record.ShouldNotBeNull();
        record.LastFailureCategory.ShouldBe(PluginIngestionStatus.CapabilityMetadataInvalid);
        record.Quarantined.ShouldBeTrue();
    }

    [Test]
    public async Task ProcessAsync_invalid_configuration_metadata_records_configuration_status()
    {
        var manifest = MinimalManifest(PluginAbiDefaults.CurrentMetadata);
        manifest.Configuration.Requirements.Add(new ManifestFileConfigurationRequirementInfo
        {
            DomainId = "Dcc",
            OptionsType = "Backend.Plugins.Configuration.Dcc.GatewayOptions",
            OptionsName = "Gateway",
            Key = "Dcc:Gateway:EndpointUrl",
            PropertyPath = "EndpointUrl",
            ClrType = "System.Uri",
            Kind = "Uri"
        });
        using var harness = PluginProcessingHarness.Create(manifest);

        await Should.ThrowAsync<InvalidDataException>(() =>
            harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None));

        var record = await harness.GetStatusRecordAsync();
        record.ShouldNotBeNull();
        record.LastFailureCategory.ShouldBe(PluginIngestionStatus.ConfigurationMetadataInvalid);
        record.Quarantined.ShouldBeTrue();
    }

    [Test]
    public async Task ProcessAsync_duplicate_registration_records_duplicate_ignored()
    {
        using var harness = PluginProcessingHarness.Create(
            MinimalManifest(PluginAbiDefaults.CurrentMetadata),
            existingVersions:
            [
                new TaskDefinitionInfo(
                    1,
                    2,
                    "StatusPlugin",
                    "app/StatusPlugin.dll",
                    new Version(1, 2, 3),
                    TaskVersionState.Registered,
                    PluginAbiDefaults.CurrentMetadata)
            ]);

        await harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None);

        var record = await harness.GetStatusRecordAsync();
        record.ShouldNotBeNull();
        record.CurrentStatus.ShouldBe(PluginIngestionStatus.DuplicateIgnored);
        harness.Repository.Verify(
            repository => repository.CreateVersion(It.IsAny<TaskVersionDomainModel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ProcessAsync_successful_registration_records_registered()
    {
        using var harness = PluginProcessingHarness.Create(MinimalManifest(PluginAbiDefaults.CurrentMetadata));

        await harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None);

        var record = await harness.GetStatusRecordAsync();
        record.ShouldNotBeNull();
        record.CurrentStatus.ShouldBe(PluginIngestionStatus.Registered);
        record.TaskVersionId.ShouldBe(100);
        record.RegisteredAt.ShouldNotBeNull();
    }

    [Test]
    public async Task ProcessAsync_quarantined_bundle_is_skipped_until_unquarantined()
    {
        using var harness = PluginProcessingHarness.Create(MinimalManifest(PluginAbiDefaults.CurrentMetadata));
        await harness.Store.AppendEventAsync(
            PluginIngestionStatusUpdate.FromPlugin(
                harness.PluginInfo.BlobName,
                harness.PluginInfo.PluginName,
                harness.PluginInfo.Version,
                PluginIngestionStatus.Quarantined,
                "bad metadata"),
            CancellationToken.None);

        await harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None);

        harness.BundleSource.Verify(
            source => source.ExtractBundleAsync(It.IsAny<PluginBlobInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await harness.Store.UnquarantineAsync(
            PluginIngestionStatusIds.FromPlugin(harness.PluginInfo),
            "operator retry",
            CancellationToken.None);

        await harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None);

        harness.BundleSource.Verify(
            source => source.ExtractBundleAsync(It.IsAny<PluginBlobInfo>(), harness.WorkspaceDirectory, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ProcessAsync_sanitizes_stored_error_messages()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "workflow-builder-secret-test-" + Guid.NewGuid().ToString("N"));
        using var harness = PluginProcessingHarness.Create(
            MinimalManifest(PluginAbiDefaults.CurrentMetadata),
            manifestException: new JsonException($"Bad JSON at {workspacePath}\\manifest.json AccountKey=abc123;Password=hunter2"));

        await Should.ThrowAsync<JsonException>(() =>
            harness.Service.ProcessAsync(harness.PluginInfo, CancellationToken.None));

        var record = await harness.GetStatusRecordAsync();
        record.ShouldNotBeNull();
        record.LastFailureCategory.ShouldBe(PluginIngestionStatus.ManifestInvalid);
        record.LastFailureMessage.ShouldNotBeNull();
        record.LastFailureMessage.ShouldNotContain(workspacePath);
        record.LastFailureMessage.ShouldNotContain("abc123");
        record.LastFailureMessage.ShouldNotContain("hunter2");
        record.LastFailureMessage.ShouldContain("<path>");
        record.LastFailureMessage.ShouldContain("<redacted>");
    }

    private static ManifestFile MinimalManifest(PluginAbiMetadata? abi) =>
        new()
        {
            DisplayName = "Status plugin",
            Abi = abi,
            OutputChannels = []
        };

    private sealed class PluginProcessingHarness : IDisposable
    {
        private PluginProcessingHarness(
            string workspaceDirectory,
            PluginBundleProcessingService service,
            InMemoryPluginIngestionStatusStore store,
            Mock<IPluginBundleSource> bundleSource,
            Mock<ITaskDefinitionRepository> repository)
        {
            WorkspaceDirectory = workspaceDirectory;
            Service = service;
            Store = store;
            BundleSource = bundleSource;
            Repository = repository;
        }

        public PluginBlobInfo PluginInfo { get; } = new("StatusPlugin_1.2.3.zip", "StatusPlugin", new Version(1, 2, 3));

        public string WorkspaceDirectory { get; }

        public PluginBundleProcessingService Service { get; }

        public InMemoryPluginIngestionStatusStore Store { get; }

        public Mock<IPluginBundleSource> BundleSource { get; }

        public Mock<ITaskDefinitionRepository> Repository { get; }

        public static PluginProcessingHarness Create(
            ManifestFile? manifest,
            PluginSchemaResult? schemas = null,
            IReadOnlyList<TaskDefinitionInfo>? existingVersions = null,
            Exception? manifestException = null,
            bool missingSchemas = false)
        {
            var workspaceDirectory = Path.Combine(
                Path.GetTempPath(),
                "workflow-builder-plugin-status-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(workspaceDirectory, "app"));
            File.WriteAllText(Path.Combine(workspaceDirectory, "app", "StatusPlugin.dll"), string.Empty);

            var bundleSource = new Mock<IPluginBundleSource>();
            bundleSource
                .Setup(candidate => candidate.ExtractBundleAsync(It.IsAny<PluginBlobInfo>(), workspaceDirectory, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var workspaceFactory = new Mock<IPluginWorkspaceFactoryUtility>();
            workspaceFactory
                .Setup(candidate => candidate.CreateWorkspaceAsync(It.IsAny<string>(), It.IsAny<Version>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(workspaceDirectory);

            var manifestParser = new Mock<IPluginManifestParsingUtility>();
            if (manifestException is not null)
            {
                manifestParser
                    .Setup(candidate => candidate.ReadAsync(workspaceDirectory, It.IsAny<CancellationToken>()))
                    .ThrowsAsync(manifestException);
            }
            else
            {
                manifestParser
                    .Setup(candidate => candidate.ReadAsync(workspaceDirectory, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(manifest);
            }

            var schemaAggregator = new Mock<IPluginSchemaAggregationUtility>();
            schemaAggregator
                .Setup(candidate => candidate.LoadAsync(workspaceDirectory, It.IsAny<CancellationToken>()))
                .ReturnsAsync(missingSchemas ? null : schemas ?? EmptySchemas());

            var reloadQueue = new Mock<IPluginReloadQueueWriter>();
            reloadQueue
                .Setup(candidate => candidate.EnqueueAsync(It.IsAny<Backend.Plugins.Communication.PluginReloadRequestMessage>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var repository = new Mock<ITaskDefinitionRepository>();
            repository
                .Setup(candidate => candidate.GetAll(It.IsAny<CancellationToken>()))
                .ReturnsAsync(existingVersions?.ToList() ?? []);
            repository
                .Setup(candidate => candidate.CreateVersion(It.IsAny<TaskVersionDomainModel>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new TaskVersionRegistrationResult(
                    TaskVersionId: 100,
                    TaskDefinitionId: 200,
                    TaskName: "StatusPlugin",
                    Version: new Version(1, 2, 3),
                    ChangeKind: PluginVersionChangeKind.Initial));

            var services = new ServiceCollection()
                .AddSingleton(repository.Object)
                .BuildServiceProvider();
            var store = new InMemoryPluginIngestionStatusStore();
            var service = new PluginBundleProcessingService(
                bundleSource.Object,
                workspaceFactory.Object,
                manifestParser.Object,
                schemaAggregator.Object,
                reloadQueue.Object,
                store,
                new PluginAbiCompatibilityService(),
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<PluginBundleProcessingService>.Instance);

            return new PluginProcessingHarness(
                workspaceDirectory,
                service,
                store,
                bundleSource,
                repository);
        }

        public Task<PluginIngestionStatusRecord?> GetStatusRecordAsync() =>
            Store.GetAsync(PluginIngestionStatusIds.FromPlugin(PluginInfo), CancellationToken.None);

        public void Dispose()
        {
            if (Directory.Exists(WorkspaceDirectory))
            {
                Directory.Delete(WorkspaceDirectory, recursive: true);
            }
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
}
