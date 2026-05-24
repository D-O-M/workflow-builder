using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Backend.Database.Relational;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Services;
using Backend.Interfaces.Utilities;
using Backend.PluginAbi;
using Backend.PluginLoader.Services;
using Backend.PluginSeeder;
using Backend.Repositories;
using Backend.Services;
using Backend.Services.PluginBundles;
using Backend.Services.PluginReloadQueue;
using Backend.Topology;
using Backend.Utilities;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

builder.Services.Configure<PluginSeedingOptions>(
    builder.Configuration.GetSection(PluginSeedingOptions.SectionName));

var sqlConnectionString = LocalDevelopmentResourceSettings.GetTasksDbConnectionString(
    builder.Configuration,
    builder.Environment);
builder.Services.AddDbContextPool<BlueprintDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));

var cosmosConnectionString = LocalDevelopmentResourceSettings.GetCosmosConnectionString(
    builder.Configuration,
    builder.Environment);
builder.Services.AddSingleton(_ => new CosmosClient(
    cosmosConnectionString,
    new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway,
        LimitToEndpoint = builder.Environment.IsDevelopment() ||
            IsLocalCosmosEndpoint(cosmosConnectionString),
        RequestTimeout = TimeSpan.FromSeconds(10),
        UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
    }));
builder.Services.AddPluginIngestionStatusStore(
    databaseName: builder.Configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]);

builder.Services.AddSingleton(_ => new BlobContainerClient(
    LocalDevelopmentResourceSettings.GetBlobConnectionString(
        builder.Configuration,
        builder.Environment),
    LocalDevelopmentResourceSettings.GetPluginsBlobContainerName(
        builder.Configuration,
        builder.Environment)));
builder.Services.AddSingleton(_ =>
{
    var connectionString = LocalDevelopmentResourceSettings.GetPluginReloadQueueConnectionString(
        builder.Configuration,
        builder.Environment);
    return new ServiceBusClient(connectionString);
});
builder.Services.AddSingleton(provider =>
{
    var client = provider.GetRequiredService<ServiceBusClient>();
    var queueName = LocalDevelopmentResourceSettings.GetPluginReloadQueueName(
        builder.Configuration,
        builder.Environment);
    return client.CreateSender(queueName);
});
builder.Services.AddSingleton<IPluginReloadQueueWriter, ServiceBusPluginReloadQueueWriter>();
builder.Services.AddScoped<ITaskDefinitionRepository, TaskDefinitionRepository>();
builder.Services.AddSingleton<IPluginBlobNameParsingUtility, PluginBlobNameParsingUtility>();
builder.Services.AddSingleton<IPluginBundleSource, BlobPluginBundleSource>();
builder.Services.AddSingleton<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();
builder.Services.AddSingleton<IPluginArchiveExtractionUtility, PluginArchiveExtractionUtility>();
builder.Services.AddSingleton<IPluginManifestParsingUtility, PluginManifestParsingUtility>();
builder.Services.AddSingleton<IPluginSchemaAggregationUtility, PluginSchemaAggregationUtility>();
builder.Services.AddSingleton<PluginAbiCompatibilityService>();
builder.Services.AddSingleton<IPluginBundleProcessingService, PluginBundleProcessingService>();
builder.Services.AddHostedService<PluginSeedingHostedService>();

await builder.Build().RunAsync();

static bool IsLocalCosmosEndpoint(string connectionString)
{
    var endpoint = GetConnectionStringSegment(connectionString, "AccountEndpoint");
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
    {
        return false;
    }

    return endpointUri.IsLoopback ||
        endpointUri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
}

static string? GetConnectionStringSegment(string connectionString, string segmentName)
{
    foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            continue;
        }

        if (segmentName.Equals(segment[..separatorIndex], StringComparison.OrdinalIgnoreCase))
        {
            return segment[(separatorIndex + 1)..];
        }
    }

    return null;
}
