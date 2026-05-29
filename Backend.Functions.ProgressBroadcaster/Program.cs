using Backend.Database.Relational;
using Backend.Functions.ProgressBroadcaster.Services;
using Backend.Repositories;
using Backend.Repositories.Processes;
using Backend.Topology;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Configuration
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables();

builder.AddServiceDefaults();
WorkflowBuilderTopologyValidator.ValidateProgressBroadcaster(builder.Configuration);

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(ProcessRealtimeProjectionTelemetry.MeterName));

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
        LimitToEndpoint = builder.Environment.IsDevelopment() ||
            IsLocalCosmosEndpoint(cosmosConnectionString),
        RequestTimeout = TimeSpan.FromSeconds(10),
        UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
    }));

builder.Services.AddRelationalDataRepositories();
builder.Services.AddProcessState(
    databaseName: builder.Configuration[WorkflowBuilderTopology.Realtime.CosmosDatabaseNameKey]!,
    processesContainerName: builder.Configuration[WorkflowBuilderTopology.Realtime.ProcessesContainerNameKey]!,
    processRealtimeSummariesContainerName: builder.Configuration[WorkflowBuilderTopology.Realtime.ProcessRealtimeSummariesContainerNameKey]!,
    processNodeRealtimeContainerName: builder.Configuration[WorkflowBuilderTopology.Realtime.ProcessNodeRealtimeContainerNameKey]!,
    tasksContainerName: builder.Configuration[WorkflowBuilderTopology.Realtime.ProcessStepsContainerNameKey]!);

var signalRBaseUrl = builder.Configuration[WorkflowBuilderTopology.Realtime.SignalRBaseUrlKey]
    ?? throw new InvalidOperationException($"{WorkflowBuilderTopology.Realtime.SignalRBaseUrlKey} is not configured.");
builder.Services.AddHttpClient(SignalRProcessGateway.HttpClientName, client =>
{
    client.BaseAddress = new Uri(EnsureTrailingSlash(signalRBaseUrl), UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(5);
    var internalPublisherBearerToken = builder.Configuration[
        WorkflowBuilderTopology.Realtime.InternalPublisherBearerTokenKey];
    if (!string.IsNullOrWhiteSpace(internalPublisherBearerToken))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            internalPublisherBearerToken);
    }

    var internalPublisherSecret = builder.Configuration[
        WorkflowBuilderTopology.Realtime.InternalPublisherSharedSecretKey];
    if (!string.IsNullOrWhiteSpace(internalPublisherSecret))
    {
        client.DefaultRequestHeaders.Add(
            WorkflowBuilderTopology.Realtime.InternalPublisherHeaderName,
            internalPublisherSecret);
    }
});

builder.Services.AddSingleton<SignalRProcessGateway>();
builder.Services.AddSingleton<BlueprintDefinitionLookupCache>();
builder.Services.AddScoped<ProcessProgressBroadcaster>();

if (builder.Configuration.GetValue(WorkflowBuilderTopology.Realtime.UseHostedChangeFeedKey, false))
{
    builder.Services.AddHostedService<ProcessStepChangeFeedBroadcasterHostedService>();
}

builder.Build().Run();

static string EnsureTrailingSlash(string baseUrl) =>
    baseUrl.EndsWith("/", StringComparison.Ordinal)
        ? baseUrl
        : $"{baseUrl}/";

static bool IsLocalCosmosEndpoint(string connectionString)
{
    var endpoint = GetConnectionStringSegment(connectionString, "AccountEndpoint");
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
    {
        return false;
    }

    return endpointUri.IsLoopback ||
        endpointUri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
}

static string? GetConnectionStringSegment(string connectionString, string segmentName)
{
    foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            continue;
        }

        if (segmentName.Equals(segment[..separatorIndex], StringComparison.OrdinalIgnoreCase))
        {
            return segment[(separatorIndex + 1)..];
        }
    }

    return null;
}
