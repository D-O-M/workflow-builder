using System.Text.Json;

namespace Backend.Topology;

public static class WorkflowBuilderTopology
{
    public static class Sql
    {
        public const string TasksDbConnectionName = "TasksDB";
        public const string SqlServerConnectionName = "SqlServer";
    }

    public static class Cosmos
    {
        public const string ResourceNameKey = "AzureCosmos:ResourceName";
        public const string ResourceNameEnvironmentVariable = "AzureCosmos__ResourceName";
        public const string ConnectionStringKey = "AzureCosmos:ConnectionString";
        public const string ConnectionNameKey = "AzureCosmos:ConnectionName";
        public const string ConnectionNameEnvironmentVariable = "AzureCosmos__ConnectionName";
        public const string DatabaseNameKey = "AzureCosmos:DatabaseName";
        public const string DatabaseNameEnvironmentVariable = "AzureCosmos__DatabaseName";
        public const string DefaultResourceName = "workflow-cosmos";
        public const string DefaultDatabaseName = "enginedb";

        public static class Containers
        {
            public static readonly CosmosContainer Processes = new("processes", "/blueprintId");
            public static readonly CosmosContainer ProcessDirectory = new("processdirectory", "/id");
            public static readonly CosmosContainer ProcessSummaries = new("processsummaries", "/partitionKey");
            public static readonly CosmosContainer ProcessSteps = new("processsteps", "/processId");
            public static readonly CosmosContainer ProcessStepDispatches = new("processstepdispatches", "/partitionKey");
            public static readonly CosmosContainer ProcessStepFinalizations = new("processstepfinalizations", "/id");
            public static readonly CosmosContainer NodeActivations = new("nodeactivations", "/processId");
            public static readonly CosmosContainer NodeJoinStates = new("nodejoinstates", "/processId");
            public static readonly CosmosContainer PhasedProcesses = new("phasedprocesses", "/phasedBlueprintDefinitionId");
            public static readonly CosmosContainer PhaseCorrelations = new("phasecorrelations", "/targetPhaseKey");
            public static readonly CosmosContainer ProcessStepProgressLeases = new("processstepprogressleases", "/id");
        }
    }

    public static class Storage
    {
        public const string ResourceNameKey = "AzureStorage:ResourceName";
        public const string ResourceNameEnvironmentVariable = "AzureStorage__ResourceName";
        public const string DefaultResourceName = "workflow-storage";
        public const string BlobConnectionName = "BlobConnection";
        public const string AzureWebJobsStorageKey = "AzureWebJobsStorage";
        public const string ConnectionStringKey = "AzureStorage:ConnectionString";
        public const string ConnectionStringEnvironmentVariable = "AzureStorage__ConnectionString";

        public static class BlobContainers
        {
            public const string PluginsContainerNameBindingExpression = "%PluginsBlobContainerName%";

            public static readonly BlobContainer Plugins = new(
                ResourceName: "plugins",
                ContainerName: "plugins",
                ResourceNameKey: "AzureStorage:BlobContainers:Plugins:ResourceName",
                ResourceNameEnvironmentVariable: "AzureStorage__BlobContainers__Plugins__ResourceName",
                ContainerNameKey: "AzureStorage:BlobContainers:Plugins:ContainerName",
                ContainerNameEnvironmentVariable: "AzureStorage__BlobContainers__Plugins__ContainerName",
                LegacyAliasKey: "PluginsBlobContainerName");

            public static readonly BlobContainer Artifacts = new(
                ResourceName: "artifacts",
                ContainerName: "artifacts",
                ResourceNameKey: "AzureStorage:BlobContainers:Artifacts:ResourceName",
                ResourceNameEnvironmentVariable: "AzureStorage__BlobContainers__Artifacts__ResourceName",
                ContainerNameKey: "AzureStorage:BlobContainers:Artifacts:ContainerName",
                ContainerNameEnvironmentVariable: "AzureStorage__BlobContainers__Artifacts__ContainerName",
                LegacyAliasKey: null);
        }
    }

    public static class ServiceBus
    {
        public const string ResourceNameKey = "AzureServiceBus:ResourceName";
        public const string ResourceNameEnvironmentVariable = "AzureServiceBus__ResourceName";
        public const string DefaultResourceName = "workflow-servicebus";
        public const string SharedConnectionName = "ServiceBus";

        public static class Queues
        {
            public static readonly ServiceBusQueue ProcessStep = new(
                LogicalName: "ProcessStepQueue",
                ResourceName: "process-step-queue",
                ConnectionName: "process-step-queue",
                QueueName: "processsteps",
                ResourceNameKey: "AzureServiceBus:Queues:ProcessStepQueue:ResourceName",
                ResourceNameEnvironmentVariable: "AzureServiceBus__Queues__ProcessStepQueue__ResourceName",
                ConnectionNameKey: "AzureServiceBus:Queues:ProcessStepQueue:ConnectionName",
                ConnectionNameEnvironmentVariable: "AzureServiceBus__Queues__ProcessStepQueue__ConnectionName",
                QueueNameKey: "AzureServiceBus:Queues:ProcessStepQueue:Name",
                QueueNameEnvironmentVariable: "AzureServiceBus__Queues__ProcessStepQueue__Name",
                LegacyQueueNameKey: "AzureServiceBus:Queues:ProcessStepQueue");

            public static readonly ServiceBusQueue PluginControl = new(
                LogicalName: "PluginControlQueue",
                ResourceName: "plugin-control-queue",
                ConnectionName: "plugin-control-queue",
                QueueName: "pluginreloadqueue",
                ResourceNameKey: "AzureServiceBus:Queues:PluginControlQueue:ResourceName",
                ResourceNameEnvironmentVariable: "AzureServiceBus__Queues__PluginControlQueue__ResourceName",
                ConnectionNameKey: "AzureServiceBus:Queues:PluginControlQueue:ConnectionName",
                ConnectionNameEnvironmentVariable: "AzureServiceBus__Queues__PluginControlQueue__ConnectionName",
                QueueNameKey: "AzureServiceBus:Queues:PluginControlQueue:Name",
                QueueNameEnvironmentVariable: "AzureServiceBus__Queues__PluginControlQueue__Name",
                LegacyQueueNameKey: "AzureServiceBus:Queues:PluginControlQueue");
        }
    }

    public static class Realtime
    {
        public const string SignalRBaseUrlKey = "Realtime:SignalRBaseUrl";
        public const string SignalRBaseUrlEnvironmentVariable = "Realtime__SignalRBaseUrl";
        public const string UseHostedChangeFeedKey = "Realtime:UseHostedChangeFeed";
        public const string UseHostedChangeFeedEnvironmentVariable = "Realtime__UseHostedChangeFeed";
        public const string RealtimeCosmosConnectionName = "RealtimeCosmos";
        public const string CosmosDatabaseNameKey = "Realtime:Cosmos:DatabaseName";
        public const string CosmosDatabaseNameEnvironmentVariable = "Realtime__Cosmos__DatabaseName";
        public const string ProcessesContainerNameKey = "Realtime:Cosmos:ProcessesContainerName";
        public const string ProcessesContainerNameEnvironmentVariable = "Realtime__Cosmos__ProcessesContainerName";
        public const string ProcessStepsContainerNameKey = "Realtime:Cosmos:TasksContainerName";
        public const string ProcessStepsContainerNameEnvironmentVariable = "Realtime__Cosmos__TasksContainerName";
        public const string LeaseContainerNameKey = "Realtime:Cosmos:LeaseContainerName";
        public const string LeaseContainerNameEnvironmentVariable = "Realtime__Cosmos__LeaseContainerName";
        public const string CosmosDatabaseNameBindingExpression = "%Realtime:Cosmos:DatabaseName%";
        public const string ProcessStepsContainerNameBindingExpression = "%Realtime:Cosmos:TasksContainerName%";
        public const string LeaseContainerNameBindingExpression = "%Realtime:Cosmos:LeaseContainerName%";
        public const string HubRoute = "/hubs/processes";
        public const string InternalSubscriptionSnapshotRoute = "internal/subscriptions/snapshot";
        public const string InternalProcessEventsRoute = "internal/process-events";
        public const string ProcessSummaryUpdatedEvent = "processSummaryUpdated";
        public const string ProcessStepsUpdatedEvent = "processStepsUpdated";
        public const string BlueprintGroupPrefix = "blueprint:";
        public const string ProcessGroupPrefix = "process:";
    }

    public static class ClientConfig
    {
        public const string SectionName = "ClientConfig";
        public const string Route = "api/client-config";
        public const string ApiBaseUrlKey = "ClientConfig:ApiBaseUrl";
        public const string ApiBaseUrlEnvironmentVariable = "ClientConfig__ApiBaseUrl";
        public const string SignalRBaseUrlKey = "ClientConfig:SignalRBaseUrl";
        public const string SignalRBaseUrlEnvironmentVariable = "ClientConfig__SignalRBaseUrl";
        public const string GrafanaBaseUrlKey = "ClientConfig:GrafanaBaseUrl";
        public const string GrafanaBaseUrlEnvironmentVariable = "ClientConfig__GrafanaBaseUrl";
    }

    public static WorkflowBuilderTopologyCatalog CreateCatalog() =>
        new(
            Cosmos: new CosmosCatalog(
                ResourceNameKey: Cosmos.ResourceNameKey,
                ConnectionNameKey: Cosmos.ConnectionNameKey,
                ConnectionStringKey: Cosmos.ConnectionStringKey,
                DatabaseNameKey: Cosmos.DatabaseNameKey,
                DefaultResourceName: Cosmos.DefaultResourceName,
                DefaultDatabaseName: Cosmos.DefaultDatabaseName,
                Containers:
                [
                    Cosmos.Containers.Processes,
                    Cosmos.Containers.ProcessDirectory,
                    Cosmos.Containers.ProcessSummaries,
                    Cosmos.Containers.ProcessSteps,
                    Cosmos.Containers.ProcessStepDispatches,
                    Cosmos.Containers.ProcessStepFinalizations,
                    Cosmos.Containers.NodeActivations,
                    Cosmos.Containers.NodeJoinStates,
                    Cosmos.Containers.PhasedProcesses,
                    Cosmos.Containers.PhaseCorrelations,
                    Cosmos.Containers.ProcessStepProgressLeases
                ]),
            Storage: new StorageCatalog(
                ResourceNameKey: Storage.ResourceNameKey,
                DefaultResourceName: Storage.DefaultResourceName,
                BlobConnectionName: Storage.BlobConnectionName,
                AzureWebJobsStorageKey: Storage.AzureWebJobsStorageKey,
                Plugins: Storage.BlobContainers.Plugins,
                Artifacts: Storage.BlobContainers.Artifacts),
            ServiceBus: new ServiceBusCatalog(
                ResourceNameKey: ServiceBus.ResourceNameKey,
                DefaultResourceName: ServiceBus.DefaultResourceName,
                SharedConnectionName: ServiceBus.SharedConnectionName,
                ProcessStepQueue: ServiceBus.Queues.ProcessStep,
                PluginControlQueue: ServiceBus.Queues.PluginControl),
            Realtime: new RealtimeCatalog(
                SignalRBaseUrlKey: Realtime.SignalRBaseUrlKey,
                UseHostedChangeFeedKey: Realtime.UseHostedChangeFeedKey,
                RealtimeCosmosConnectionName: Realtime.RealtimeCosmosConnectionName,
                CosmosDatabaseNameKey: Realtime.CosmosDatabaseNameKey,
                ProcessesContainerNameKey: Realtime.ProcessesContainerNameKey,
                ProcessStepsContainerNameKey: Realtime.ProcessStepsContainerNameKey,
                LeaseContainerNameKey: Realtime.LeaseContainerNameKey,
                HubRoute: Realtime.HubRoute,
                InternalSubscriptionSnapshotRoute: Realtime.InternalSubscriptionSnapshotRoute,
                InternalProcessEventsRoute: Realtime.InternalProcessEventsRoute,
                ProcessSummaryUpdatedEvent: Realtime.ProcessSummaryUpdatedEvent,
                ProcessStepsUpdatedEvent: Realtime.ProcessStepsUpdatedEvent,
                BlueprintGroupPrefix: Realtime.BlueprintGroupPrefix,
                ProcessGroupPrefix: Realtime.ProcessGroupPrefix),
            ClientConfig: new ClientConfigCatalog(
                SectionName: ClientConfig.SectionName,
                Route: ClientConfig.Route,
                ApiBaseUrlKey: ClientConfig.ApiBaseUrlKey,
                SignalRBaseUrlKey: ClientConfig.SignalRBaseUrlKey,
                GrafanaBaseUrlKey: ClientConfig.GrafanaBaseUrlKey));

    public static string CreateCatalogJson(bool indented = true)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented
        };

        return JsonSerializer.Serialize(CreateCatalog(), options) + Environment.NewLine;
    }
}

public sealed record CosmosContainer(string Name, string PartitionKey);

public sealed record BlobContainer(
    string ResourceName,
    string ContainerName,
    string ResourceNameKey,
    string ResourceNameEnvironmentVariable,
    string ContainerNameKey,
    string ContainerNameEnvironmentVariable,
    string? LegacyAliasKey);

public sealed record ServiceBusQueue(
    string LogicalName,
    string ResourceName,
    string ConnectionName,
    string QueueName,
    string ResourceNameKey,
    string ResourceNameEnvironmentVariable,
    string ConnectionNameKey,
    string ConnectionNameEnvironmentVariable,
    string QueueNameKey,
    string QueueNameEnvironmentVariable,
    string LegacyQueueNameKey);

public sealed record WorkflowBuilderTopologyCatalog(
    CosmosCatalog Cosmos,
    StorageCatalog Storage,
    ServiceBusCatalog ServiceBus,
    RealtimeCatalog Realtime,
    ClientConfigCatalog ClientConfig);

public sealed record CosmosCatalog(
    string ResourceNameKey,
    string ConnectionNameKey,
    string ConnectionStringKey,
    string DatabaseNameKey,
    string DefaultResourceName,
    string DefaultDatabaseName,
    IReadOnlyList<CosmosContainer> Containers);

public sealed record StorageCatalog(
    string ResourceNameKey,
    string DefaultResourceName,
    string BlobConnectionName,
    string AzureWebJobsStorageKey,
    BlobContainer Plugins,
    BlobContainer Artifacts);

public sealed record ServiceBusCatalog(
    string ResourceNameKey,
    string DefaultResourceName,
    string SharedConnectionName,
    ServiceBusQueue ProcessStepQueue,
    ServiceBusQueue PluginControlQueue);

public sealed record RealtimeCatalog(
    string SignalRBaseUrlKey,
    string UseHostedChangeFeedKey,
    string RealtimeCosmosConnectionName,
    string CosmosDatabaseNameKey,
    string ProcessesContainerNameKey,
    string ProcessStepsContainerNameKey,
    string LeaseContainerNameKey,
    string HubRoute,
    string InternalSubscriptionSnapshotRoute,
    string InternalProcessEventsRoute,
    string ProcessSummaryUpdatedEvent,
    string ProcessStepsUpdatedEvent,
    string BlueprintGroupPrefix,
    string ProcessGroupPrefix);

public sealed record ClientConfigCatalog(
    string SectionName,
    string Route,
    string ApiBaseUrlKey,
    string SignalRBaseUrlKey,
    string GrafanaBaseUrlKey);
