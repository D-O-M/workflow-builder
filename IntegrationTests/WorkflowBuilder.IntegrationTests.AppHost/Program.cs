using Aspire.Hosting.Azure;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var cosmos = AddCosmos(builder);
var sqlPassword = builder.AddParameter("SqlServerPassword", secret: true);
var sqlServer = AddSqlServer(builder, sqlPassword);
var sqlDatabase = sqlServer.AddDatabase("TasksDB");
var (storage, blobs, blobContainer) = AddStorage(builder);
var (serviceBus, processStepQueue) = AddServiceBus(builder);

var cosmosConnectionName = GetRequiredConfigurationValue(
    builder.Configuration,
    "AzureCosmos:ConnectionName");
var processStepQueueConnectionName = GetRequiredConfigurationValue(
    builder.Configuration,
    "AzureServiceBus:Queues:ProcessStepQueue:ConnectionName");
var startEngine = builder.Configuration.GetValue("IntegrationTests:StartEngine", true);

builder.AddProject<Projects.Backend_API>("backend-api")
    .WithReference(cosmos, connectionName: cosmosConnectionName)
    .WaitFor(cosmos)
    .WithReference(blobs)
    .WaitFor(blobContainer)
    .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
    .WaitFor(serviceBus)
    .WithReference(sqlDatabase)
    .WaitFor(sqlDatabase);

if (startEngine)
{
    builder.AddProject<Projects.Backend_Engine>("backend-engine")
        .WithReference(cosmos, connectionName: cosmosConnectionName)
        .WaitFor(cosmos)
        .WithReference(blobs)
        .WaitFor(blobContainer)
        .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
        .WaitFor(serviceBus)
        .WithReference(sqlDatabase)
        .WaitFor(sqlDatabase);
}

builder.AddAzureFunctionsProject<Projects.Backend_Functions_PluginDetector>("plugin-detector")
    .WithHostStorage(storage)
    .WaitFor(blobContainer)
    .WithReference(sqlDatabase)
    .WaitFor(sqlDatabase);

builder.Build().Run();

static IResourceBuilder<AzureCosmosDBResource> AddCosmos(
    IDistributedApplicationBuilder builder)
{
    var cosmosResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        "AzureCosmos:ResourceName");

#pragma warning disable ASPIRECOSMOSDB001
    var cosmos = builder.AddAzureCosmosDB(cosmosResourceName)
        .RunAsPreviewEmulator(emulator => emulator.WithGatewayPort(8081));
#pragma warning restore ASPIRECOSMOSDB001

    return cosmos;
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
    IResourceBuilder<AzureBlobStorageContainerResource> blobContainer)
    AddStorage(IDistributedApplicationBuilder builder)
{
    var storageResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        "AzureStorage:ResourceName");
    var pluginsBlobResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        "AzureStorage:BlobContainers:Plugins:ResourceName");
    var pluginsBlobContainerName = GetRequiredConfigurationValue(
        builder.Configuration,
        "AzureStorage:BlobContainers:Plugins:ContainerName");

    var storage = builder.AddAzureStorage(storageResourceName)
        .RunAsEmulator();

    var blobs = storage.AddBlobs("BlobConnection");
    var blobContainer = storage.AddBlobContainer(
        pluginsBlobResourceName,
        pluginsBlobContainerName);

    return (storage, blobs, blobContainer);
}

static (IResourceBuilder<AzureServiceBusResource> serviceBus,
    IResourceBuilder<AzureServiceBusQueueResource> processStepQueue)
    AddServiceBus(IDistributedApplicationBuilder builder)
{
    var serviceBusResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        "AzureServiceBus:ResourceName");
    var processStepQueueResourceName = GetRequiredConfigurationValue(
        builder.Configuration,
        "AzureServiceBus:Queues:ProcessStepQueue:ResourceName");
    var processStepQueueName = GetRequiredConfigurationValue(
        builder.Configuration,
        "AzureServiceBus:Queues:ProcessStepQueue:Name");

    var serviceBus = builder.AddAzureServiceBus(serviceBusResourceName)
        .RunAsEmulator();

    var processStepQueue = serviceBus.AddServiceBusQueue(
        processStepQueueResourceName,
        processStepQueueName);

    return (serviceBus, processStepQueue);
}

static string GetRequiredConfigurationValue(
    IConfiguration configuration,
    string key) =>
    configuration.GetValue<string>(key)
    ?? throw new InvalidOperationException($"Configuration value '{key}' is not configured.");
