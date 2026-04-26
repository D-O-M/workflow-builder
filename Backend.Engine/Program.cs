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
using Backend.Services.ProcessStepQueue;
using Backend.Utilities;
using Backend.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("WorkflowBuilder.Engine.Processes"));

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.Configure<DebuggerAutoAttachOptions>(
    builder.Configuration.GetSection("DebuggerAutoAttach"));

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
        LimitToEndpoint = builder.Environment.IsDevelopment(),
        RequestTimeout = TimeSpan.FromSeconds(10),
        UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
    }));
builder.Services.AddRelationalDataRepositories();
builder.Services.AddProcessState();

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
builder.Services.AddSingleton<PluginRuntimeOrchestrator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PluginRuntimeOrchestrator>());
builder.Services.AddSingleton<IAsyncDelay, DefaultAsyncDelay>();
builder.Services.AddSingleton<IDebuggerAttachmentGuard, DebuggerAttachmentGuard>();
builder.Services.AddSingleton<IOperatingSystemInformation, OperatingSystemInformation>();
builder.Services.AddSingleton<IVisualStudioInstanceProvider, RotVisualStudioInstanceProvider>();
builder.Services.AddTransient<PluginRuntimeAgent>();
builder.Services.AddSingleton<IPluginArchiveExtractionUtility, PluginArchiveExtractionUtility>();
builder.Services.AddScoped<IPluginWorkspaceFactoryUtility, PluginWorkspaceFactoryUtility>();
builder.Services.AddSingleton<IPluginBundleSource, BlobPluginBundleSource>();

var serviceBusConnectionString = LocalDevelopmentResourceSettings.GetProcessStepQueueConnectionString(
    builder.Configuration,
    builder.Environment);
var pluginReloadConnectionString = LocalDevelopmentResourceSettings.GetPluginReloadQueueConnectionString(
    builder.Configuration,
    builder.Environment);

var processStepQueueName = LocalDevelopmentResourceSettings.GetProcessStepQueueName(
    builder.Configuration,
    builder.Environment);
var pluginReloadQueueName = LocalDevelopmentResourceSettings.GetPluginReloadQueueName(
    builder.Configuration,
    builder.Environment);

builder.Services.AddSingleton(_ => new ProcessStepQueueBinding(
    new ServiceBusClient(serviceBusConnectionString),
    processStepQueueName));
builder.Services.AddSingleton(_ => new PluginReloadQueueBinding(
    new ServiceBusClient(pluginReloadConnectionString),
    pluginReloadQueueName));
builder.Services.AddSingleton<IProcessStepQueueReader>(provider =>
{
    var binding = provider.GetRequiredService<ProcessStepQueueBinding>();
    return new ServiceBusProcessStepQueueReader(binding.Client.CreateReceiver(binding.QueueName));
});
builder.Services.AddSingleton<IProcessStepQueueWriter>(provider =>
{
    var binding = provider.GetRequiredService<ProcessStepQueueBinding>();
    return new ServiceBusProcessStepQueueWriter(
        binding.Client.CreateSender(binding.QueueName),
        provider.GetRequiredService<ILogger<ServiceBusProcessStepQueueWriter>>());
});
builder.Services.AddSingleton<IPluginReloadQueueReader>(provider =>
{
    var binding = provider.GetRequiredService<PluginReloadQueueBinding>();
    return new ServiceBusPluginReloadQueueReader(binding.Client.CreateReceiver(binding.QueueName));
});

builder.Services.AddTransient<JobFinalizationService>();
builder.Services.AddTransient<NodeActivationService>();
builder.Services.AddSingleton<ProcessLifecycleTelemetry>();
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

internal sealed class ProcessStepQueueBinding(ServiceBusClient client, string queueName)
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
