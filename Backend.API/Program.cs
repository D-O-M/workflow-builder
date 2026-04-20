using Azure.Messaging.ServiceBus;
using Backend.Database.Relational;
using Backend.API.Observability;
using Backend.API.Services;
using Backend.API.Infrastructure;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Backend.Services.WorkflowTaskQueue;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using System.Text.Json;

namespace Backend.API;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // Add services to the container.
        builder.Services.AddSingleton<IWorkflowDbContextOptionsConfigurator, SqlServerWorkflowDbContextOptionsConfigurator>();
        builder.Services.AddDbContextPool<WorkflowDbContext>((serviceProvider, options) =>
        {
            serviceProvider
                .GetRequiredService<IWorkflowDbContextOptionsConfigurator>()
                .Configure(options, serviceProvider);
        });

        builder.Services.AddSingleton(sp => new CosmosClient(
            LocalDevelopmentResourceSettings.GetCosmosConnectionString(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IHostEnvironment>()),
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
            }));
        builder.Services.AddRelationalDataRepositories();
        builder.Services.AddWorkflowExecutionState();
        builder.Services.AddHostedService<WorkflowExecutionStateWarmupHostedService>();

        builder.Services.AddSingleton(sp => new ServiceBusClient(
            LocalDevelopmentResourceSettings.GetWorkflowTaskQueueConnectionString(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IHostEnvironment>())));
        builder.Services.AddSingleton(provider =>
        {
            var client = provider.GetRequiredService<ServiceBusClient>();
            var configuration = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            return client.CreateSender(LocalDevelopmentResourceSettings.GetWorkflowTaskQueueName(
                configuration,
                environment));
        });
        builder.Services.AddSingleton<IWorkflowTaskQueueWriter, ServiceBusWorkflowTaskQueueWriter>();
        builder.Services.AddScoped<BlueprintExecutionService>();
        builder.Services.AddScoped<BlueprintPortabilityService>();
        builder.Services.AddScoped<WorkflowExecutionQueryService>();
        builder.Services.AddScoped<ObservabilityQueryService>();
        builder.Services.Configure<ClientRuntimeConfigOptions>(
            builder.Configuration.GetSection(ClientRuntimeConfigOptions.SectionName));
        builder.Services.Configure<ObservabilityOptions>(
            builder.Configuration.GetSection(ObservabilityOptions.SectionName));
        builder.Services.AddHttpClient<LokiQueryClient>((serviceProvider, client) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var baseUrl = GetRequiredConfigurationValue(configuration, "Observability:LokiBaseUrl");
            client.BaseAddress = new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient<PrometheusQueryClient>((serviceProvider, client) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var baseUrl = GetRequiredConfigurationValue(configuration, "Observability:PrometheusBaseUrl");
            client.BaseAddress = new Uri(EnsureTrailingSlash(baseUrl), UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        builder.Services.AddControllers();

builder.Services.AddScoped<Backend.API.Validation.Blueprints.IBlueprintVersionValidator>(sp =>
    new Backend.API.Validation.Blueprints.BlueprintVersionValidatorPipeline(
        new Backend.API.Validation.Blueprints.IBlueprintVersionValidator[]
        {
            new Backend.API.Validation.Blueprints.BlueprintVersionGraphValidator()
        }));
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAngularDevClient", policy =>
            {
                policy.SetIsOriginAllowed(IsAllowedDevelopmentOrigin)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials();
            });
        });

        var app = builder.Build();

        app.MapDefaultEndpoints();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();

            // Map the Scalar UI (default route is typically /scalar/v1)
            app.MapScalarApiReference();
        }

        app.UseAuthorization();
        app.UseCors("AllowAngularDevClient");

        app.MapControllers();

        app.Run();
    }

    private static string EnsureTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : $"{baseUrl}/";

    private static string GetRequiredConfigurationValue(IConfiguration configuration, string key) =>
        configuration[key]
        ?? throw new InvalidOperationException($"Configuration value '{key}' is not configured.");

    private static bool IsAllowedDevelopmentOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback
            || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }
}
