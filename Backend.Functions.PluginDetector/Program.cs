using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Services;
using Backend.Interfaces.Utilities;
using Backend.Database.Relational;
using Backend.Repositories;
using Backend.Services;
using Backend.Services.PluginReloadQueue;
using Backend.Services.PluginBundles;
using Backend.Utilities;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Backend.PluginLoader.Services;
using Backend.Functions.PluginDetector;
using Backend.PluginAbi;
using Backend.Topology;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.AddServiceDefaults();
WorkflowBuilderTopologyValidator.ValidatePluginDetector(builder.Configuration);

var sqlConnectionString = builder.Configuration.GetConnectionString(WorkflowBuilderTopology.Sql.TasksDbConnectionName);
builder.Services.AddDbContextPool<BlueprintDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));

builder.Services.AddSingleton(_ =>
{
    var blobConnectionString = builder.Configuration[WorkflowBuilderTopology.Storage.AzureWebJobsStorageKey]
        ?? throw new InvalidOperationException($"{WorkflowBuilderTopology.Storage.AzureWebJobsStorageKey} is not configured.");
    var containerName = builder.Configuration[WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey]!;

    return new BlobContainerClient(blobConnectionString, containerName);
});

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
builder.Services.AddScoped<IPluginDiscoveryService, PluginDiscoveryService>();
builder.Services.AddSingleton<IPluginBlobNameParsingUtility, PluginBlobNameParsingUtility>();
builder.Services.AddSingleton<IPluginBundleSource, BlobPluginBundleSource>();
builder.Services.AddSingleton<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();
builder.Services.AddSingleton<IPluginArchiveExtractionUtility, PluginArchiveExtractionUtility>();
builder.Services.AddSingleton<IPluginManifestParsingUtility, PluginManifestParsingUtility>();
builder.Services.AddSingleton<IPluginSchemaAggregationUtility, PluginSchemaAggregationUtility>();
builder.Services.AddSingleton<PluginAbiCompatibilityService>();
builder.Services.AddSingleton<IPluginBundleProcessingService, PluginBundleProcessingService>();
builder.Services.AddHostedService<PluginBlobReconciliationHostedService>();

builder.Build().Run();
