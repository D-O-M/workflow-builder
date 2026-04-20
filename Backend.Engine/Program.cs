using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Backend.Engine;
using Backend.Engine.Debugging;
using Backend.Database.Relational;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Utilities;
using Backend.Repositories;
using Backend.Services.PluginReloadQueue;
using Backend.Services.PluginBundles;
using Backend.Services.WorkflowTaskQueue;
using Backend.Utilities;
using Backend.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("WorkflowBuilder.Engine.Execution"));

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
        UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
    }));
builder.Services.AddRelationalDataRepositories();
builder.Services.AddWorkflowExecutionState();
builder.Services.AddHostedService<WorkflowExecutionStateWarmupHostedService>();

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
builder.Services.AddSingleton<NanoServiceOrchestrator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<NanoServiceOrchestrator>());
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
var pluginReloadConnectionString = LocalDevelopmentResourceSettings.GetPluginReloadQueueConnectionString(
    builder.Configuration,
    builder.Environment);

var workflowTaskQueueName = LocalDevelopmentResourceSettings.GetWorkflowTaskQueueName(
    builder.Configuration,
    builder.Environment);
var pluginReloadQueueName = LocalDevelopmentResourceSettings.GetPluginReloadQueueName(
    builder.Configuration,
    builder.Environment);

builder.Services.AddSingleton(_ => new WorkflowTaskQueueBinding(
    new ServiceBusClient(serviceBusConnectionString),
    workflowTaskQueueName));
builder.Services.AddSingleton(_ => new PluginReloadQueueBinding(
    new ServiceBusClient(pluginReloadConnectionString),
    pluginReloadQueueName));
builder.Services.AddSingleton<IWorkflowTaskQueueReader>(provider =>
{
    var binding = provider.GetRequiredService<WorkflowTaskQueueBinding>();
    return new ServiceBusWorkflowTaskQueueReader(binding.Client.CreateReceiver(binding.QueueName));
});
builder.Services.AddSingleton<IWorkflowTaskQueueWriter>(provider =>
{
    var binding = provider.GetRequiredService<WorkflowTaskQueueBinding>();
    return new ServiceBusWorkflowTaskQueueWriter(
        binding.Client.CreateSender(binding.QueueName),
        provider.GetRequiredService<ILogger<ServiceBusWorkflowTaskQueueWriter>>());
});
builder.Services.AddSingleton<IPluginReloadQueueReader>(provider =>
{
    var binding = provider.GetRequiredService<PluginReloadQueueBinding>();
    return new ServiceBusPluginReloadQueueReader(binding.Client.CreateReceiver(binding.QueueName));
});

builder.Services.AddTransient<JobFinalizationService>();
builder.Services.AddSingleton<ExecutionLifecycleTelemetry>();
builder.Services.AddSingleton<JobManager>();

builder.Services.AddHostedService<QueueConsumer>();
builder.Services.AddHostedService<PluginReloadConsumer>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();
app.MapControllers();

app.Run();

internal sealed class WorkflowTaskQueueBinding(ServiceBusClient client, string queueName)
    : IAsyncDisposable
{
    public ServiceBusClient Client { get; } = client;

    public string QueueName { get; } = queueName;

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

internal sealed class PluginReloadQueueBinding(ServiceBusClient client, string queueName)
    : IAsyncDisposable
{
    public ServiceBusClient Client { get; } = client;

    public string QueueName { get; } = queueName;

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
