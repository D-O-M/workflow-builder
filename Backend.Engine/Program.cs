using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Backend.Engine;
using Backend.Engine.Debugging;
using Backend.Engine.Capabilities;
using Backend.Database.Relational;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Utilities;
using Backend.PluginAbi;
using Backend.Repositories;
using Backend.Services.PluginReloadQueue;
using Backend.Services.PluginBundles;
using Backend.Services.ProcessStepQueue;
using Backend.Services.PluginRuntimeStatus;
using Backend.Topology;
using Backend.Utilities;
using Backend.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
WorkflowBuilderTopologyValidator.ValidateEngine(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(ProcessLifecycleTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics
        .AddMeter("WorkflowBuilder.Engine.Processes")
        .AddMeter(EngineProjectionTelemetry.MeterName)
        .AddMeter("WorkflowBuilder.Cosmos")
        .AddMeter(Backend.Repositories.Processes.ProcessRealtimeProjectionTelemetry.MeterName)
        .AddMeter(Backend.Repositories.Processes.ProcessSummaryProjectionTelemetry.MeterName)
        .AddMeter(ProcessStepDispatchTelemetry.MeterName)
        .AddMeter(ProcessStepLockRenewalTelemetry.MeterName)
        .AddMeter(PluginResultFinalizationTelemetry.MeterName)
        .AddMeter(PluginRuntimeAgentTelemetry.MeterName)
        .AddMeter(PluginAgentHealthTelemetry.MeterName));

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.Configure<DebuggerAutoAttachOptions>(
    builder.Configuration.GetSection("DebuggerAutoAttach"));
builder.Services.Configure<CollectionIteratorOptions>(
    builder.Configuration.GetSection("CollectionIterator"));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>()
        .GetSection("ProcessStepDispatch")
        .Get<ProcessStepDispatchOptions>()
    ?? new ProcessStepDispatchOptions());
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>()
        .GetSection(ProcessStepQueueLockRenewalOptions.SectionName)
        .Get<ProcessStepQueueLockRenewalOptions>()
    ?? new ProcessStepQueueLockRenewalOptions());
var engineProjectionOptions = builder.Configuration
    .GetSection(EngineProjectionOptions.SectionName)
    .Get<EngineProjectionOptions>()
    ?? new EngineProjectionOptions();
var synchronousProjectionUpdatesEnabled = engineProjectionOptions.SynchronousEngineUpdatesEnabled;
builder.Services.AddSingleton(engineProjectionOptions);
builder.Services.AddSingleton<EngineProjectionTelemetry>();

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
builder.Services.AddProcessState(
    databaseName: builder.Configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]!,
    registerProjectionServices: synchronousProjectionUpdatesEnabled);

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
builder.Services.AddSingleton<ICommandCapabilityProbe, ProcessCommandCapabilityProbe>();
builder.Services.AddSingleton<IEndpointCapabilityProbe, HttpEndpointCapabilityProbe>();
builder.Services.AddSingleton<ICapabilityValidationService, CapabilityValidationService>();
builder.Services.AddSingleton<IChildProcessTracker, ChildProcessTracker>();
builder.Services.AddSingleton<IPluginManifestParsingUtility, PluginManifestParsingUtility>();
builder.Services.AddSingleton<IPluginRuntimeCapabilityService, PluginRuntimeCapabilityService>();
builder.Services.AddSingleton<IPluginRuntimeConfigurationService, PluginRuntimeConfigurationService>();
builder.Services.AddSingleton<PluginAbiCompatibilityService>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IConfiguration>()
        .GetSection("PluginRuntime")
        .Get<PluginRuntimeOptions>()
        ?? new PluginRuntimeOptions();
    options.Validate();
    return options;
});
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>()
        .GetSection("PluginRuntimeStatus")
        .Get<PluginRuntimeStatusOptions>()
    ?? new PluginRuntimeStatusOptions());
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>()
        .GetSection(PluginAgentHealthPolicyOptions.SectionName)
        .Get<PluginAgentHealthPolicyOptions>()
    ?? new PluginAgentHealthPolicyOptions());
builder.Services.AddSingleton<EngineInstanceInfo>();
builder.Services.AddSingleton<PluginTaskVersionRuntimeStatusPublisher>();
builder.Services.AddSingleton<PluginRuntimeCatalog>();
builder.Services.AddSingleton<PluginAgentHealthTelemetry>();
builder.Services.AddSingleton<PluginAgentHealthPolicy>();
builder.Services.AddSingleton<PluginAgentHealthEventBus>();
builder.Services.AddSingleton<PluginRuntimeOrchestrator>();
builder.Services.AddSingleton<IPluginRuntimeReloader>(provider => provider.GetRequiredService<PluginRuntimeOrchestrator>());
builder.Services.AddSingleton<IPluginAgentHealthDecisionHandler>(provider => provider.GetRequiredService<PluginRuntimeOrchestrator>());
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
builder.Services.AddSingleton<IProcessStepQueueProcessor>(provider =>
{
    var binding = provider.GetRequiredService<ProcessStepQueueBinding>();
    var configuration = provider.GetRequiredService<IConfiguration>();
    var maxConcurrentCalls = Math.Clamp(
        configuration.GetValue("ProcessStepQueue:MaxConcurrentMessages", 8),
        1,
        512);
    var prefetchCount = Math.Clamp(
        configuration.GetValue("ProcessStepQueue:PrefetchCount", maxConcurrentCalls * 4),
        0,
        10_000);
    var maxAutoLockRenewalMinutes = Math.Max(
        1,
        configuration.GetValue("ProcessStepQueue:MaxAutoLockRenewalMinutes", 5));

    var processor = binding.Client.CreateProcessor(
        binding.QueueName,
        new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = maxConcurrentCalls,
            PrefetchCount = prefetchCount,
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(maxAutoLockRenewalMinutes)
        });

    return new ServiceBusProcessStepQueueProcessor(
        processor,
        provider.GetRequiredService<ILogger<ServiceBusProcessStepQueueProcessor>>());
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

builder.Services.AddTransient<StepFinalizationService>();
builder.Services.AddTransient<IStepFinalizationService>(provider => provider.GetRequiredService<StepFinalizationService>());
builder.Services.AddSingleton<StepClaimService>();
builder.Services.AddSingleton<ActivationPlanner>();
builder.Services.AddTransient<ActivationPersistenceService>();
builder.Services.AddTransient<CollectionIteratorExecutionService>();
builder.Services.AddTransient<ConditionalBranchExecutionService>();
builder.Services.AddTransient<ICollectionIteratorExecutionService>(provider => provider.GetRequiredService<CollectionIteratorExecutionService>());
builder.Services.AddSingleton<ProcessLifecycleTelemetry>();
builder.Services.AddSingleton<ProcessStepDispatchTelemetry>();
builder.Services.AddSingleton<ProcessStepLockRenewalTelemetry>();
builder.Services.AddSingleton<PluginResultFinalizationTelemetry>();
builder.Services.AddSingleton<PluginRuntimeAgentTelemetry>();
builder.Services.AddSingleton<ActiveStepLockRenewalService>();
builder.Services.AddSingleton<ProcessStepDispatchService>();
builder.Services.AddSingleton<IProcessStepDispatcher>(provider =>
    provider.GetRequiredService<ProcessStepDispatchService>());
builder.Services.AddSingleton<StepExecutionService>();
builder.Services.AddSingleton<IStepExecutionService>(provider => provider.GetRequiredService<StepExecutionService>());
builder.Services.AddSingleton<IPluginAgentHealthDecisionHandler>(provider => provider.GetRequiredService<StepExecutionService>());
builder.Services.AddSingleton<PluginAgentExitNotificationService>();
builder.Services.AddSingleton<StepMessageHandler>();
builder.Services.AddSingleton<PluginResultFinalizationQueue>();

builder.Services.AddHostedService<PluginResultFinalizationWorker>();
builder.Services.AddHostedService<PluginAgentHealthWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PluginRuntimeOrchestrator>());
builder.Services.AddHostedService<PluginRuntimeStatusHeartbeatWorker>();
builder.Services.AddHostedService<QueueConsumer>();
builder.Services.AddHostedService<PluginReloadConsumer>();

var app = builder.Build();
_ = app.Services.GetRequiredService<EngineProjectionTelemetry>();

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
