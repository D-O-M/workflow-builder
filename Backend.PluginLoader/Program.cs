using Azure.Storage.Blobs;
using Backend.Relational.DAL;
using Backend.Interfaces.Repositories;
using Backend.PluginLoader.Services;
using Backend.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Create builder (this replaces Host.CreateDefaultBuilder())
var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

// ✅ Configuration (you can directly access builder.Configuration)
builder.Configuration
    .AddUserSecrets<Program>(optional: false)
    .AddEnvironmentVariables();

// ✅ Services (no ConfigureServices callback — just use builder.Services)
var sqlConnectionString = builder.Configuration.GetConnectionString("TasksDB");
builder.Services.AddDbContextPool<WorkflowDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));

builder.Services.AddSingleton(_ =>
{
    var blobConnectionString = builder.Configuration.GetConnectionString("BlobConnection");
    var containerName = builder.Configuration["AzureStorage:BlobContainers:Plugins:ContainerName"]!;

    var blobContainerClient = new BlobContainerClient(
        blobConnectionString,
        containerName);

    return blobContainerClient;
});

builder.Services.AddScoped<IWorkflowTaskRepository, WorkflowTaskRepository>();
builder.Services.AddSingleton<IPluginBlobNameParser, PluginBlobNameParser>();
builder.Services.AddSingleton<IPluginDiscoveryService, PluginDiscoveryService>();
builder.Services.AddSingleton<IPluginWorkspaceFactory, PluginWorkspaceFactory>();
builder.Services.AddSingleton<IPluginArchiveExtractor, PluginArchiveExtractor>();
builder.Services.AddSingleton<IPluginManifestReader, PluginManifestReader>();
builder.Services.AddSingleton<IPluginSchemaAggregator, PluginSchemaAggregator>();
builder.Services.AddSingleton<IPluginBundleProcessor, PluginReceptionService>();
builder.Services.AddHostedService<PluginLoaderService>();

// Build & run host
var app = builder.Build();
await app.RunAsync();

/*builder.Services.AddAzureClients(async clientBuilder =>
{
    // Register clients for each service
    clientBuilder.AddSecretClient(new Uri("<key_vault_url>"));
    clientBuilder.AddBlobServiceClient(new Uri("<storage_url>"));
    clientBuilder.AddServiceBusClientWithNamespace(
        "<your_namespace>.servicebus.windows.net");

    // Set a credential for all clients to use by default
    DefaultAzureCredential credential = new();
clientBuilder.UseCredential(credential);

    // Register a subclient for each Service Bus Queue
    List<string> queueNames = await GetQueueNames(credential);
    foreach (string queue in queueNames)
    {
        clientBuilder.AddClient<ServiceBusSender, ServiceBusClientOptions>(
            (_, _, provider) => provider.GetService<ServiceBusClient>()
                .CreateSender(queue)).WithName(queue);
    }

    // Register a custom client factory
    clientBuilder.AddClient<AzureOpenAIClient, AzureOpenAIClientOptions>(
        (options, _, _) => new AzureOpenAIClient(
            new Uri("<url_here>"), credential, options)); 
});*/
