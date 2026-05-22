using System.Text.Json;
using Backend.Topology;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Backend.Topology.Tests;

public sealed class WorkflowBuilderTopologyValidatorTests
{
    [Test]
    public void Catalog_uses_canonical_runtime_names()
    {
        var catalog = WorkflowBuilderTopology.CreateCatalog();

        catalog.Cosmos.DefaultDatabaseName.ShouldBe("enginedb");
        catalog.Cosmos.Containers.ShouldContain(container =>
            container.Name == WorkflowBuilderTopology.Cosmos.Containers.ProcessDirectory.Name &&
            container.PartitionKey == "/id");
        catalog.Cosmos.Containers.ShouldContain(container =>
            container.Name == WorkflowBuilderTopology.Cosmos.Containers.ProcessSummaries.Name &&
            container.PartitionKey == "/partitionKey");
        catalog.Cosmos.Containers.ShouldContain(container =>
            container.Name == WorkflowBuilderTopology.Cosmos.Containers.ProcessRealtimeSummaries.Name &&
            container.PartitionKey == "/id");
        catalog.Cosmos.Containers.ShouldContain(container =>
            container.Name == WorkflowBuilderTopology.Cosmos.Containers.ProcessNodeRealtime.Name &&
            container.PartitionKey == "/processId");
        catalog.Cosmos.Containers.ShouldContain(container =>
            container.Name == WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name &&
            container.PartitionKey == "/processId");
        catalog.Cosmos.Containers.ShouldContain(container =>
            container.Name == WorkflowBuilderTopology.Cosmos.Containers.ProcessStepDispatches.Name &&
            container.PartitionKey == "/partitionKey");
        catalog.Storage.Plugins.ContainerName.ShouldBe("plugins");
        catalog.ServiceBus.ProcessStepQueue.QueueName.ShouldBe("processsteps");
        catalog.Realtime.HubRoute.ShouldBe("/hubs/processes");
        catalog.ClientConfig.Route.ShouldBe("api/client-config");
    }

    [Test]
    public void Catalog_json_snapshot_matches_checked_in_script_catalog()
    {
        var expectedPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "BuildScripts",
            "workflowbuilder-topology.json"));

        File.Exists(expectedPath).ShouldBeTrue($"Expected topology catalog at {expectedPath}.");
        var expected = NormalizeJson(File.ReadAllText(expectedPath));
        var actual = NormalizeJson(WorkflowBuilderTopology.CreateCatalogJson());

        actual.ShouldBe(expected);
    }

    [Test]
    public void ValidateApi_accepts_complete_configuration()
    {
        Should.NotThrow(() => WorkflowBuilderTopologyValidator.ValidateApi(ApiConfiguration()));
    }

    [Test]
    public void ValidateApi_accepts_missing_dispatch_enabled_setting()
    {
        Should.NotThrow(() => WorkflowBuilderTopologyValidator.ValidateApi(ApiConfiguration()));
    }

    [Test]
    public void ValidateApi_rejects_disabled_process_step_dispatch()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateApi(ApiConfiguration(overrides: new()
            {
                ["ProcessStepDispatch:Enabled"] = "false"
            })));

        ex.Errors.ShouldContain(error => error.Contains("ProcessStepDispatch:Enabled", StringComparison.Ordinal));
    }

    [Test]
    public void ValidateApi_rejects_invalid_dispatch_enabled_value()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateApi(ApiConfiguration(overrides: new()
            {
                ["ProcessStepDispatch:Enabled"] = "maybe"
            })));

        ex.Errors.ShouldContain(error => error.Contains("ProcessStepDispatch:Enabled", StringComparison.Ordinal));
    }

    [Test]
    public void ValidateApi_rejects_missing_sql()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateApi(ApiConfiguration(remove: "ConnectionStrings:TasksDB")));

        ex.Errors.ShouldContain(error => error.Contains("Missing SQL connection string", StringComparison.Ordinal));
    }

    [Test]
    public void ValidateApi_rejects_missing_process_step_queue()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateApi(ApiConfiguration(remove: WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey)));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateApi_rejects_placeholder_values()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateApi(ApiConfiguration(overrides: new()
            {
                [WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlKey] = "<signalr>"
            })));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateApi_rejects_missing_client_config()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateApi(ApiConfiguration(remove: WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlKey)));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateEngine_accepts_complete_configuration()
    {
        Should.NotThrow(() => WorkflowBuilderTopologyValidator.ValidateEngine(EngineConfiguration()));
    }

    [Test]
    public void ValidateEngine_rejects_disabled_process_step_dispatch()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateEngine(EngineConfiguration(overrides: new()
            {
                ["ProcessStepDispatch:Enabled"] = "false"
            })));

        ex.Errors.ShouldContain(error => error.Contains("ProcessStepDispatch:Enabled", StringComparison.Ordinal));
    }

    [Test]
    public void ValidateEngine_rejects_invalid_dispatch_enabled_value()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateEngine(EngineConfiguration(overrides: new()
            {
                ["ProcessStepDispatch:Enabled"] = "<dispatch-enabled>"
            })));

        ex.Errors.ShouldContain(error => error.Contains("ProcessStepDispatch:Enabled", StringComparison.Ordinal));
    }

    [Test]
    public void ValidateEngine_rejects_missing_plugin_control_queue()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateEngine(EngineConfiguration(remove: WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameKey)));

        ex.Errors.ShouldContain(error => error.Contains("PluginControlQueue", StringComparison.Ordinal));
    }

    [Test]
    public void ValidateEngine_rejects_plugin_blob_container_mismatch()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateEngine(EngineConfiguration(overrides: new()
            {
                [WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey] = "pluginblob"
            })));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateEngine_rejects_missing_artifacts_container()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateEngine(EngineConfiguration(remove: WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameKey)));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidatePluginDetector_accepts_complete_configuration()
    {
        Should.NotThrow(() => WorkflowBuilderTopologyValidator.ValidatePluginDetector(PluginDetectorConfiguration()));
    }

    [Test]
    public void ValidatePluginDetector_rejects_missing_host_storage()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidatePluginDetector(PluginDetectorConfiguration(remove: WorkflowBuilderTopology.Storage.AzureWebJobsStorageKey)));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Storage.AzureWebJobsStorageKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidatePluginDetector_rejects_missing_plugin_container()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidatePluginDetector(PluginDetectorConfiguration(remove: WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey)));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidatePluginDetector_rejects_blob_trigger_alias_mismatch()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidatePluginDetector(PluginDetectorConfiguration(overrides: new()
            {
                [WorkflowBuilderTopology.Storage.BlobContainers.Plugins.LegacyAliasKey!] = "pluginblob"
            })));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.LegacyAliasKey!, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateProgressBroadcaster_accepts_complete_configuration()
    {
        Should.NotThrow(() => WorkflowBuilderTopologyValidator.ValidateProgressBroadcaster(ProgressBroadcasterConfiguration()));
    }

    [Test]
    public void ValidateProgressBroadcaster_rejects_missing_realtime_cosmos()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateProgressBroadcaster(ProgressBroadcasterConfiguration(remove: $"ConnectionStrings:{WorkflowBuilderTopology.Realtime.RealtimeCosmosConnectionName}")));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Realtime.RealtimeCosmosConnectionName, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateProgressBroadcaster_rejects_invalid_signalr_url()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateProgressBroadcaster(ProgressBroadcasterConfiguration(overrides: new()
            {
                [WorkflowBuilderTopology.Realtime.SignalRBaseUrlKey] = "not-a-url"
            })));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Realtime.SignalRBaseUrlKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateProgressBroadcaster_rejects_noncanonical_container_names()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateProgressBroadcaster(ProgressBroadcasterConfiguration(overrides: new()
            {
                [WorkflowBuilderTopology.Realtime.ProcessStepsContainerNameKey] = "othersteps"
            })));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Realtime.ProcessStepsContainerNameKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateProgressBroadcaster_rejects_missing_realtime_projection_container_names()
    {
        var ex = Should.Throw<WorkflowBuilderTopologyValidationException>(() =>
            WorkflowBuilderTopologyValidator.ValidateProgressBroadcaster(ProgressBroadcasterConfiguration(remove: WorkflowBuilderTopology.Realtime.ProcessRealtimeSummariesContainerNameKey)));

        ex.Errors.ShouldContain(error => error.Contains(WorkflowBuilderTopology.Realtime.ProcessRealtimeSummariesContainerNameKey, StringComparison.Ordinal));
    }

    [Test]
    public void ValidateSignalR_accepts_static_topology()
    {
        Should.NotThrow(WorkflowBuilderTopologyValidator.ValidateSignalR);
    }

    private static IConfiguration ApiConfiguration(
        Dictionary<string, string?>? overrides = null,
        string? remove = null) =>
        BuildConfiguration(Apply(BaseConfiguration(), overrides, remove));

    private static IConfiguration EngineConfiguration(
        Dictionary<string, string?>? overrides = null,
        string? remove = null)
    {
        var values = BaseConfiguration();
        values[$"ConnectionStrings:{WorkflowBuilderTopology.Storage.BlobConnectionName}"] = "UseDevelopmentStorage=true";
        values[WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey] = WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerName;
        values[WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameKey] = WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerName;
        values[WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameKey] = WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionName;
        values[$"ConnectionStrings:{WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionName}"] = "Endpoint=sb://test/;";
        values[WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameKey] = WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueName;
        return BuildConfiguration(Apply(values, overrides, remove));
    }

    private static IConfiguration PluginDetectorConfiguration(
        Dictionary<string, string?>? overrides = null,
        string? remove = null)
    {
        var values = BaseConfiguration(includeClientConfig: false, includeProcessStepQueue: false);
        values[WorkflowBuilderTopology.Storage.AzureWebJobsStorageKey] = "UseDevelopmentStorage=true";
        values[WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey] = WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerName;
        values[WorkflowBuilderTopology.Storage.BlobContainers.Plugins.LegacyAliasKey!] = WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerName;
        values[WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameKey] = WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionName;
        values[$"ConnectionStrings:{WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionName}"] = "Endpoint=sb://test/;";
        values[WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameKey] = WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueName;
        return BuildConfiguration(Apply(values, overrides, remove));
    }

    private static IConfiguration ProgressBroadcasterConfiguration(
        Dictionary<string, string?>? overrides = null,
        string? remove = null)
    {
        var values = BaseConfiguration(includeClientConfig: false, includeProcessStepQueue: false);
        values[$"ConnectionStrings:{WorkflowBuilderTopology.Realtime.RealtimeCosmosConnectionName}"] = "AccountEndpoint=https://test/;";
        values[WorkflowBuilderTopology.Realtime.SignalRBaseUrlKey] = "http://localhost:5000";
        values[WorkflowBuilderTopology.Realtime.CosmosDatabaseNameKey] = WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;
        values[WorkflowBuilderTopology.Realtime.ProcessesContainerNameKey] = WorkflowBuilderTopology.Cosmos.Containers.Processes.Name;
        values[WorkflowBuilderTopology.Realtime.ProcessStepsContainerNameKey] = WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name;
        values[WorkflowBuilderTopology.Realtime.ProcessRealtimeSummariesContainerNameKey] = WorkflowBuilderTopology.Cosmos.Containers.ProcessRealtimeSummaries.Name;
        values[WorkflowBuilderTopology.Realtime.ProcessNodeRealtimeContainerNameKey] = WorkflowBuilderTopology.Cosmos.Containers.ProcessNodeRealtime.Name;
        values[WorkflowBuilderTopology.Realtime.LeaseContainerNameKey] = WorkflowBuilderTopology.Cosmos.Containers.ProcessStepProgressLeases.Name;
        return BuildConfiguration(Apply(values, overrides, remove));
    }

    private static Dictionary<string, string?> BaseConfiguration(
        bool includeClientConfig = true,
        bool includeProcessStepQueue = true)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"ConnectionStrings:{WorkflowBuilderTopology.Sql.TasksDbConnectionName}"] = "Server=(local);",
            [WorkflowBuilderTopology.Cosmos.ConnectionNameKey] = WorkflowBuilderTopology.Cosmos.DefaultDatabaseName,
            [$"ConnectionStrings:{WorkflowBuilderTopology.Cosmos.DefaultDatabaseName}"] = "AccountEndpoint=https://test/;",
            [WorkflowBuilderTopology.Cosmos.DatabaseNameKey] = WorkflowBuilderTopology.Cosmos.DefaultDatabaseName
        };

        if (includeProcessStepQueue)
        {
            values[WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameKey] = WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionName;
            values[$"ConnectionStrings:{WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionName}"] = "Endpoint=sb://test/;";
            values[WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey] = WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueName;
        }

        if (includeClientConfig)
        {
            values[WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlKey] = "http://localhost:5000";
            values[WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlKey] = "http://localhost:3000";
        }

        return values;
    }

    private static Dictionary<string, string?> Apply(
        Dictionary<string, string?> values,
        Dictionary<string, string?>? overrides,
        string? remove)
    {
        if (remove is not null)
        {
            values.Remove(remove);
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static string NormalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(
            document.RootElement,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}
