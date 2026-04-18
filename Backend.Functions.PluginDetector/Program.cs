using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.Database.Relational;
using Backend.Repositories;
using Backend.Services;
using Backend.Services.PluginReloadQueue;
using Backend.Services.PluginBundles;
using Backend.Utilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Backend.PluginLoader.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration(config =>
    {
        config.AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables();
    })
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var sqlConnectionString = context.Configuration.GetConnectionString("TasksDB");
        services.AddDbContextPool<WorkflowDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        services.AddSingleton(_ =>
        {
            var blobConnectionString = context.Configuration["AzureWebJobsStorage"]
                ?? throw new InvalidOperationException("AzureWebJobsStorage is not configured.");
            var containerName = context.Configuration["AzureStorage:BlobContainers:Plugins:ContainerName"]!;

            return new BlobContainerClient(blobConnectionString, containerName);
        });

        services.AddSingleton(_ =>
        {
            var connectionString = LocalDevelopmentResourceSettings.GetPluginReloadQueueConnectionString(
                context.Configuration,
                context.HostingEnvironment);
            return new ServiceBusClient(connectionString);
        });
        services.AddSingleton(provider =>
        {
            var client = provider.GetRequiredService<ServiceBusClient>();
            var queueName = LocalDevelopmentResourceSettings.GetPluginReloadQueueName(
                context.Configuration,
                context.HostingEnvironment);
            return client.CreateSender(queueName);
        });
        services.AddSingleton<IPluginReloadQueueWriter, ServiceBusPluginReloadQueueWriter>();

        services.AddScoped<IWorkflowTaskRepository, WorkflowTaskRepository>();
        services.AddSingleton<IPluginBlobNameParsingUtility, PluginBlobNameParsingUtility>();
        services.AddSingleton<IPluginBundleSource, BlobPluginBundleSource>();
        services.AddSingleton<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();
        services.AddSingleton<IPluginArchiveExtractionUtility, PluginArchiveExtractionUtility>();
        services.AddSingleton<IPluginManifestParsingUtility, PluginManifestParsingUtility>();
        services.AddSingleton<IPluginSchemaAggregationUtility, PluginSchemaAggregationUtility>();
        services.AddSingleton<IPluginBundleProcessingService, PluginBundleProcessingService>();
    })
    .Build();

host.Run();
