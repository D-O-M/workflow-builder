using Azure.Storage.Blobs;
using Backend.Interfaces.Repositories;
using Backend.Interfaces.Utilities;
using Backend.PluginLoader.Services;
using Backend.Relational.DAL;
using Backend.Repositories;
using Backend.Services;
using Backend.Utilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            var blobConnectionString = context.Configuration["Aspire:Azure:Storage:Blobs:pluginblob:ConnectionString"];
            //context.Configuration.GetConnectionString("BlobConnection");
            var containerName = context.Configuration["AzureStorage:BlobContainers:Plugins:ContainerName"]!;

            return new BlobContainerClient(blobConnectionString, containerName);
        });

        services.AddScoped<IWorkflowTaskRepository, WorkflowTaskRepository>();
        services.AddSingleton<IPluginBlobNameParsingUtility, PluginBlobNameParsingUtility>();
        services.AddSingleton<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();
        services.AddSingleton<IPluginArchiveExtractionUtility, PluginArchiveExtractionUtility>();
        services.AddSingleton<IPluginManifestParsingUtility, PluginManifestParsingUtility>();
        services.AddSingleton<IPluginSchemaAggregationUtility, PluginSchemaAggregationUtility>();
        services.AddSingleton<IPluginBundleProcessingService, PluginBundleProcessingService>();
    })
    .Build();

host.Run();
