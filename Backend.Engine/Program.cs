using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Backend.Engine;
using Backend.Engine.Debugging;
using Backend.Interfaces.Utilities;
using Backend.Database.Relational;
using Backend.Repositories;
using Backend.Utilities;
using Microsoft.EntityFrameworkCore;
using Backend.Interfaces.Repositories;

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

builder.Services.AddRelationalDataRepositories();

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
builder.Services.AddScoped<IWorkflowTaskRepository, WorkflowTaskRepository>();
builder.Services.AddScoped<IWorkflowBlueprintRepository, WorkflowBlueprintRepository>();

var serviceBusConnectionString = builder.Configuration.GetConnectionString(
    builder.Configuration["AzureServiceBus:Queues:WorkflowTaskQueue:ResourceName"]!)
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
