using Backend.Database.Relational;
using Backend.Functions.ProgressBroadcaster.Services;
using Backend.Repositories;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.ApplicationInsights;
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

        var sqlConnectionString = LocalDevelopmentResourceSettings.GetTasksDbConnectionString(
            context.Configuration,
            context.HostingEnvironment);
        services.AddDbContextPool<WorkflowDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        var cosmosConnectionString = context.Configuration.GetConnectionString("RealtimeCosmos")
            ?? LocalDevelopmentResourceSettings.GetCosmosConnectionString(
                context.Configuration,
                context.HostingEnvironment);
        services.AddSingleton(_ => new CosmosClient(
            cosmosConnectionString,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
            }));

        services.AddRelationalDataRepositories();
        services.AddWorkflowExecutionState(
            databaseName: context.Configuration["Realtime:Cosmos:DatabaseName"] ?? "enginedb",
            workflowsContainerName: context.Configuration["Realtime:Cosmos:WorkflowsContainerName"] ?? "workflows",
            tasksContainerName: context.Configuration["Realtime:Cosmos:TasksContainerName"] ?? "tasks");

        var signalRBaseUrl = context.Configuration["Realtime:SignalRBaseUrl"]
            ?? "http://localhost:5181";
        services.AddHttpClient(SignalRExecutionGateway.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(EnsureTrailingSlash(signalRBaseUrl), UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddSingleton<SignalRExecutionGateway>();
        services.AddSingleton<BlueprintDefinitionLookupCache>();
    })
    .Build();

host.Run();

static string EnsureTrailingSlash(string baseUrl) =>
    baseUrl.EndsWith("/", StringComparison.Ordinal)
        ? baseUrl
        : $"{baseUrl}/";
