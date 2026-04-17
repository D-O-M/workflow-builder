using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Backend.Engine;
using Backend.Engine.Debugging;
using Backend.Database.Relational;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Utilities;
using Backend.Repositories;
using Backend.Services.PluginBundles;
using Backend.Services.WorkflowTaskQueue;
using Backend.Utilities;
using Backend.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.Configure<DebuggerAutoAttachOptions>(
    builder.Configuration.GetSection("DebuggerAutoAttach"));

var sqlConnectionString = LocalDevelopmentResourceSettings.GetTasksDbConnectionString(
    builder.Configuration,
    builder.Environment);
builder.Services.AddDbContextPool<WorkflowDbContext>(options =>
    options.UseSqlServer(sqlConnectionString));

var cosmosConnectionString = LocalDevelopmentResourceSettings.GetCosmosConnectionString(
    builder.Configuration,
    builder.Environment);

builder.Services.AddSingleton(_ => new CosmosClient(
    cosmosConnectionString,
    new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway,
        UseSystemTextJsonSerializerWithOptions = JsonSerializerOptions.Web
    }));
builder.Services.AddRelationalDataRepositories();
builder.Services.AddWorkflowExecutionState();

builder.Services.AddSingleton(_ =>
{
    var blobConnectionString = LocalDevelopmentResourceSettings.GetBlobConnectionString(
        builder.Configuration,
        builder.Environment);
    var containerName = LocalDevelopmentResourceSettings.GetPluginsBlobContainerName(
        builder.Configuration,
        builder.Environment);

    var blobContainerClient = new BlobContainerClient(
        blobConnectionString,
        containerName);

    return blobContainerClient;
});

builder.Services.AddSingleton<IAgentManager, AgentManager>();
builder.Services.AddSingleton<IChildProcessTracker, ChildProcessTracker>();
builder.Services.AddHostedService<NanoServiceOrchestrator>();
builder.Services.AddSingleton<IAsyncDelay, DefaultAsyncDelay>();
builder.Services.AddSingleton<IDebuggerAttachmentGuard, DebuggerAttachmentGuard>();
builder.Services.AddSingleton<IOperatingSystemInformation, OperatingSystemInformation>();
builder.Services.AddSingleton<IVisualStudioInstanceProvider, RotVisualStudioInstanceProvider>();
builder.Services.AddTransient<NanoServiceAgent>();
builder.Services.AddSingleton<IPluginArchiveExtractionUtility, PluginArchiveExtractionUtility>();
builder.Services.AddScoped<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();
builder.Services.AddSingleton<IPluginBundleSource, BlobPluginBundleSource>();

var serviceBusConnectionString = LocalDevelopmentResourceSettings.GetWorkflowTaskQueueConnectionString(
    builder.Configuration,
    builder.Environment);

var workflowTaskQueueName = LocalDevelopmentResourceSettings.GetWorkflowTaskQueueName(
    builder.Configuration,
    builder.Environment);

builder.Services.AddSingleton(_ => new ServiceBusClient(serviceBusConnectionString));
builder.Services.AddSingleton(provider =>
{
    var client = provider.GetRequiredService<ServiceBusClient>();
    return client.CreateReceiver(workflowTaskQueueName);
});
builder.Services.AddSingleton<IWorkflowTaskQueueReader, ServiceBusWorkflowTaskQueueReader>();

builder.Services.AddSingleton(provider =>
{
    var client = provider.GetRequiredService<ServiceBusClient>();
    return client.CreateSender(workflowTaskQueueName);
});
builder.Services.AddSingleton<IWorkflowTaskQueueWriter, ServiceBusWorkflowTaskQueueWriter>();

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
