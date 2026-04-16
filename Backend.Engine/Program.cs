using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Backend.Engine;
using Backend.Engine.Debugging;
using Backend.Database.Relational;
using Backend.Interfaces.Utilities;
using Backend.Repositories;
using Backend.Utilities;
using Backend.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.Configure<DebuggerAutoAttachOptions>(
    builder.Configuration.GetSection("DebuggerAutoAttach"));

var sqlConnectionString = builder.Configuration.GetConnectionString("TasksDB");
builder.Services.AddDbContextPool<WorkflowDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));

var cosmosConnectionName = builder.Configuration["AzureCosmos:ConnectionName"]
    ?? throw new InvalidOperationException("Azure Cosmos connection name is not configured.");
var workflowTaskQueueConnectionName = builder.Configuration["AzureServiceBus:Queues:WorkflowTaskQueue:ConnectionName"]
    ?? throw new InvalidOperationException("Workflow task queue connection name is not configured.");

builder.AddAzureCosmosClient(cosmosConnectionName);
builder.Services.AddRelationalDataRepositories();
builder.Services.AddWorkflowExecutionState();

builder.Services.AddSingleton(_ =>
{
    var blobConnectionString = builder.Configuration.GetConnectionString("BlobConnection");
    var containerName = builder.Configuration["AzureStorage:BlobContainers:Plugins:ContainerName"]!;

    var blobContainerClient = new BlobContainerClient(
        blobConnectionString,
        containerName);

    return blobContainerClient;
});

builder.Services.AddSingleton<EngineInstanceInfo>(_ => new());
builder.Services.AddSingleton<IAgentManager, AgentManager>();
builder.Services.AddSingleton<IChildProcessTracker, ChildProcessTracker>();
builder.Services.AddHostedService<NanoServiceOrchestrator>();
builder.Services.AddSingleton<IAsyncDelay, DefaultAsyncDelay>();
builder.Services.AddSingleton<IDebuggerAttachmentGuard, DebuggerAttachmentGuard>();
builder.Services.AddSingleton<IOperatingSystemInformation, OperatingSystemInformation>();
builder.Services.AddSingleton<IVisualStudioInstanceProvider, RotVisualStudioInstanceProvider>();
builder.Services.AddSingleton<VisualStudioDebuggerAttacher>();
builder.Services.AddTransient<NanoServiceAgent>();
builder.Services.AddScoped<IPluginArchiveExtractionUtility, PluginArchiveExtractionUtility>();
builder.Services.AddScoped<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();

var serviceBusConnectionString = builder.Configuration.GetConnectionString(workflowTaskQueueConnectionName)
    ?? throw new InvalidOperationException("ServiceBus connection string is not configured.");

var workflowTaskQueueName = builder.Configuration["AzureServiceBus:Queues:WorkflowTaskQueue:Name"]
    ?? throw new InvalidOperationException("Workflow task queue name is not configured.");

builder.Services.AddSingleton(_ => new ServiceBusClient(serviceBusConnectionString));
builder.Services.AddSingleton(provider =>
{
    var client = provider.GetRequiredService<ServiceBusClient>();
    return client.CreateReceiver(workflowTaskQueueName);
});

builder.Services.AddSingleton(provider =>
{
    var client = provider.GetRequiredService<ServiceBusClient>();
    return client.CreateSender(workflowTaskQueueName);
});

builder.Services.AddTransient<JobFinalizationService>();
builder.Services.AddSingleton<JobManager>();

builder.Services.AddHostedService<QueueConsumer>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
