using Aspire.Hosting.Azure;
using Backend.Topology;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var (cosmos, cosmosDb) = AddCosmos(builder);
var sqlPassword = builder.AddParameter("SqlServerPassword", secret: true);
var sqlServer = AddSqlServer(builder, sqlPassword);
var sqlDatabase = sqlServer.AddDatabase(WorkflowBuilderTopology.Sql.TasksDbConnectionName);
var (storage, blobs, pluginsBlobContainer, artifactsBlobContainer) = AddStorage(builder);
var (serviceBus, processStepQueue, pluginControlQueue) = AddServiceBus(builder);

var cosmosConnectionName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.Cosmos.ConnectionNameKey);
var cosmosResourceName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.Cosmos.ResourceNameKey);
var cosmosDatabaseName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.Cosmos.DatabaseNameKey);
var pluginsBlobContainerName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey);
var artifactsBlobContainerName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameKey);
var processStepQueueConnectionName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameKey);
var processStepQueueName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey);
var pluginControlQueueConnectionName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameKey);
var pluginControlQueueName = GetRequiredConfigurationValue(
    builder.Configuration,
    WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameKey);
var startEngine = builder.Configuration.GetValue("IntegrationTests:StartEngine", true);

var signalR = builder.AddProject<Projects.Backend_SignalR>("backend-signalr")
    .WithHttpEndpoint(
        name: "http",
        env: "ASPNETCORE_HTTP_PORTS")
    .WithEnvironment("Authentication__Enabled", "false")
    .WithEnvironment("Authentication__Keycloak__Enabled", "false");
var signalRHttpEndpoint = signalR.GetEndpoint("http");

builder.AddProject<Projects.Backend_API>("backend-api")
    .WithReference(cosmos, connectionName: cosmosConnectionName)
    .WaitFor(cosmosDb)
    .WithReference(blobs)
    .WaitFor(pluginsBlobContainer)
    .WaitFor(artifactsBlobContainer)
    .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
    .WaitFor(serviceBus)
    .WithReference(sqlDatabase)
    .WaitFor(sqlDatabase)
    .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
    .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
    .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
    .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameEnvironmentVariable, processStepQueueConnectionName)
    .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameEnvironmentVariable, processStepQueueName)
    .WithEnvironment(WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlEnvironmentVariable, signalRHttpEndpoint)
    .WithEnvironment(WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlEnvironmentVariable, "http://127.0.0.1:9")
    .WithEnvironment("Authentication__Enabled", "false")
    .WithEnvironment("Authentication__Keycloak__Enabled", "false")
    .WithEnvironment("ClientConfig__Authentication__Enabled", "false");

if (startEngine)
{
    builder.AddProject<Projects.Backend_Engine>("backend-engine")
        .WithReference(cosmos, connectionName: cosmosConnectionName)
        .WaitFor(cosmosDb)
        .WithReference(blobs)
        .WaitFor(pluginsBlobContainer)
        .WaitFor(artifactsBlobContainer)
        .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
        .WithReference(pluginControlQueue, connectionName: pluginControlQueueConnectionName)
        .WaitFor(serviceBus)
        .WithReference(sqlDatabase)
        .WaitFor(sqlDatabase)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
        .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameEnvironmentVariable, pluginsBlobContainerName)
        .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameEnvironmentVariable, artifactsBlobContainerName)
        .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameEnvironmentVariable, processStepQueueConnectionName)
        .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameEnvironmentVariable, processStepQueueName)
        .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameEnvironmentVariable, pluginControlQueueConnectionName)
        .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameEnvironmentVariable, pluginControlQueueName);

    builder.AddProject<Projects.Backend_Dispatcher>("backend-dispatcher")
        .WithReference(cosmos, connectionName: cosmosConnectionName)
        .WaitFor(cosmosDb)
        .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
        .WaitFor(serviceBus)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
        .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameEnvironmentVariable, processStepQueueConnectionName)
        .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameEnvironmentVariable, processStepQueueName);

    builder.AddProject<Projects.Backend_ProjectionWorker>("backend-projection-worker")
        .WithReference(cosmos, connectionName: cosmosConnectionName)
        .WaitFor(cosmosDb)
        .WithReference(sqlDatabase)
        .WaitFor(sqlDatabase)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName);

    builder.AddProject<Projects.Backend_Maintenance>("backend-maintenance")
        .WithReference(cosmos, connectionName: cosmosConnectionName)
        .WaitFor(cosmosDb)
        .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
        .WaitFor(serviceBus)
        .WithReference(sqlDatabase)
        .WaitFor(sqlDatabase)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
        .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
        .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameEnvironmentVariable, processStepQueueConnectionName)
        .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameEnvironmentVariable, processStepQueueName);
}

builder.AddAzureFunctionsProject<Projects.Backend_Functions_PluginDetector>("plugin-detector")
    .WithHostStorage(storage)
    .WaitFor(pluginsBlobContainer)
    .WithReference(pluginControlQueue, connectionName: pluginControlQueueConnectionName)
    .WaitFor(serviceBus)
    .WithReference(sqlDatabase)
    .WaitFor(sqlDatabase)
    .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameEnvironmentVariable, pluginsBlobContainerName)
    .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.LegacyAliasKey!, pluginsBlobContainerName)
    .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameEnvironmentVariable, pluginControlQueueConnectionName)
    .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameEnvironmentVariable, pluginControlQueueName);

builder.Build().Run();

static (
    IResourceBuilder<AzureCosmosDBResource> account,
    IResourceBuilder<AzureCosmosDBDatabaseResource> database)
    AddCosmos(
    IDistributedApplicationBuilder builder)
{
    var cosmosResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.Cosmos.ResourceNameKey);
    var cosmosDatabaseName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.Cosmos.DatabaseNameKey);

#pragma warning disable ASPIRECOSMOSDB001
    var cosmos = builder.AddAzureCosmosDB(cosmosResourceName)
        .RunAsPreviewEmulator(emulator => emulator.WithGatewayPort(8081));
#pragma warning restore ASPIRECOSMOSDB001

    var database = cosmos.AddCosmosDatabase(cosmosDatabaseName);
    foreach (var container in WorkflowBuilderTopology.CreateCatalog().Cosmos.Containers)
    {
        database.AddContainer(container.Name, container.PartitionKey);
    }

    return (cosmos, database);
}

static IResourceBuilder<SqlServerServerResource> AddSqlServer(
    IDistributedApplicationBuilder builder,
    IResourceBuilder<ParameterResource> password) =>
    builder.AddSqlServer("sqlserver", password)
        .WithEnvironment("ACCEPT_EULA", "Y")
        .WithEnvironment("MSSQL_SA_PASSWORD", password);

static (
    IResourceBuilder<AzureStorageResource> storage,
    IResourceBuilder<AzureBlobStorageResource> blobStorage,
    IResourceBuilder<AzureBlobStorageContainerResource> pluginsBlobContainer,
    IResourceBuilder<AzureBlobStorageContainerResource> artifactsBlobContainer)
    AddStorage(IDistributedApplicationBuilder builder)
{
    var storageResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.Storage.ResourceNameKey);
    var pluginsBlobResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ResourceNameKey);
    var pluginsBlobContainerName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey);
    var artifactsBlobResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ResourceNameKey);
    var artifactsBlobContainerName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameKey);

    var storage = builder.AddAzureStorage(storageResourceName)
        .RunAsEmulator();

    var blobs = storage.AddBlobs(WorkflowBuilderTopology.Storage.BlobConnectionName);
    var pluginsBlobContainer = storage.AddBlobContainer(
        pluginsBlobResourceName,
        pluginsBlobContainerName);
    var artifactsBlobContainer = storage.AddBlobContainer(
        artifactsBlobResourceName,
        artifactsBlobContainerName);

    return (storage, blobs, pluginsBlobContainer, artifactsBlobContainer);
}

static (IResourceBuilder<AzureServiceBusResource> serviceBus,
    IResourceBuilder<AzureServiceBusQueueResource> processStepQueue,
    IResourceBuilder<AzureServiceBusQueueResource> pluginControlQueue)
    AddServiceBus(IDistributedApplicationBuilder builder)
{
    var serviceBusResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.ServiceBus.ResourceNameKey);
    var processStepQueueResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ResourceNameKey);
    var processStepQueueName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey);
    var pluginControlQueueResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ResourceNameKey);
    var pluginControlQueueName = GetRequiredConfigurationValue(
        builder.Configuration,
        WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameKey);

    var serviceBus = builder.AddAzureServiceBus(serviceBusResourceName)
        .RunAsEmulator();

    var processStepQueue = serviceBus.AddServiceBusQueue(
        processStepQueueResourceName,
        processStepQueueName);
    var pluginControlQueue = serviceBus.AddServiceBusQueue(
        pluginControlQueueResourceName,
        pluginControlQueueName);

    return (serviceBus, processStepQueue, pluginControlQueue);
}

static string GetRequiredConfigurationValue(
    IConfiguration configuration,
    string key) =>
    configuration.GetValue<string>(key)
    ?? throw new InvalidOperationException($"Configuration value '{key}' is not configured.");
