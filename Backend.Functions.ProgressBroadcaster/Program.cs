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
        services.AddCommonWorkerOpenTelemetry(
            context.Configuration,
            context.HostingEnvironment.ApplicationName);

        var sqlConnectionString = LocalDevelopmentResourceSettings.GetTasksDbConnectionString(
            context.Configuration,
            context.HostingEnvironment);
        services.AddDbContextPool<BlueprintDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        var cosmosConnectionString = LocalDevelopmentResourceSettings.GetCosmosConnectionString(
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
        services.AddProcessState(
            databaseName: context.Configuration["Realtime:Cosmos:DatabaseName"] ?? "enginedb",
            processesContainerName: context.Configuration["Realtime:Cosmos:ProcessesContainerName"] ?? "processes",
            tasksContainerName: context.Configuration["Realtime:Cosmos:TasksContainerName"] ?? "processsteps");

        var signalRBaseUrl = context.Configuration["Realtime:SignalRBaseUrl"]
            ?? throw new InvalidOperationException("Realtime:SignalRBaseUrl is not configured.");
        services.AddHttpClient(SignalRProcessGateway.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(EnsureTrailingSlash(signalRBaseUrl), UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddSingleton<SignalRProcessGateway>();
        services.AddSingleton<BlueprintDefinitionLookupCache>();
    })
    .Build();

host.Run();

static string EnsureTrailingSlash(string baseUrl) =>
    baseUrl.EndsWith("/", StringComparison.Ordinal)
        ? baseUrl
        : $"{baseUrl}/";
