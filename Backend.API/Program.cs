using Azure.Messaging.ServiceBus;
using Backend.Database.Relational;
using Backend.API.Observability;
using Backend.API.Services;
using Backend.API.Infrastructure;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Backend.Services.ProcessStepQueue;
using Backend.Services.PluginRuntimeStatus;
using Backend.Security.Authentication;
using Backend.Security.Authorization;
using Backend.Security.Cors;
using Backend.Topology;
using Microsoft.Azure.Cosmos;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using System.Text.Json;

namespace Backend.API;

public class Program
{
    public static void Main(string[] args)
    {
        AspireEndpointEnvironment.PreferAspireHttpPortsOverInheritedUrls();
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        WorkflowBuilderTopologyValidator.ValidateApi(builder.Configuration);

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(ProcessStepDispatchTelemetry.MeterName)
                .AddMeter("WorkflowBuilder.Cosmos")
                .AddMeter(Backend.Repositories.Processes.ProcessRealtimeProjectionTelemetry.MeterName)
                .AddMeter(Backend.Repositories.Processes.ProcessSummaryProjectionTelemetry.MeterName)
                .AddMeter("WorkflowBuilder.API.ProcessStart"));

        // Add services to the container.
        builder.Services.AddSingleton<IBlueprintDbContextOptionsConfigurator, SqlServerBlueprintDbContextOptionsConfigurator>();
        builder.Services.AddDbContextPool<BlueprintDbContext>((serviceProvider, options) =>
        {
            serviceProvider
                .GetRequiredService<IBlueprintDbContextOptionsConfigurator>()
                .Configure(options, serviceProvider);
        });

        builder.Services.AddSingleton(sp => new CosmosClient(
            LocalDevelopmentResourceSettings.GetCosmosConnectionString(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IHostEnvironment>()),
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = sp.GetRequiredService<IHostEnvironment>().IsDevelopment(),
                RequestTimeout = TimeSpan.FromSeconds(10),
                UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
            }));
        builder.Services.AddRelationalDataRepositories();
        builder.Services.AddProcessState(
            databaseName: builder.Configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]!);

        builder.Services.AddSingleton(sp => new ServiceBusClient(
            LocalDevelopmentResourceSettings.GetProcessStepQueueConnectionString(
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<IHostEnvironment>())));
        builder.Services.AddSingleton(provider =>
        {
            var client = provider.GetRequiredService<ServiceBusClient>();
            var configuration = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            return client.CreateSender(LocalDevelopmentResourceSettings.GetProcessStepQueueName(
                configuration,
                environment));
        });
        builder.Services.AddSingleton<IProcessStepQueueWriter, ServiceBusProcessStepQueueWriter>();
        builder.Services.AddSingleton<ProcessStartTelemetry>();
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>()
                .GetSection("ProcessStepDispatch")
                .Get<ProcessStepDispatchOptions>()
            ?? new ProcessStepDispatchOptions());
        builder.Services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>()
                .GetSection("PluginRuntimeStatus")
                .Get<PluginRuntimeStatusOptions>()
            ?? new PluginRuntimeStatusOptions());
        builder.Services.AddSingleton<ProcessStepDispatchTelemetry>();
        builder.Services.AddSingleton<ProcessStepDispatchService>();
        builder.Services.AddSingleton<IProcessStepDispatcher>(provider =>
            provider.GetRequiredService<ProcessStepDispatchService>());
        builder.Services.AddScoped<BlueprintProcessService>();
        builder.Services.AddScoped<PhasedBlueprintProcessService>();
        builder.Services.AddScoped<TaskCatalogQueryService>();
        builder.Services.AddScoped<BlueprintPortabilityService>();
        builder.Services.AddScoped<ProcessQueryService>();
        builder.Services.AddScoped<ObservabilityQueryService>();
        builder.Services.Configure<ClientRuntimeConfigOptions>(
            builder.Configuration.GetSection(WorkflowBuilderTopology.ClientConfig.SectionName));
        builder.Services.Configure<ConditionalBranchAuthoringOptions>(
            builder.Configuration.GetSection(ConditionalBranchAuthoringOptions.SectionName));
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
        builder.Services.AddMemoryCache();
        builder.Services.AddWorkflowSecurity(builder.Configuration, builder.Environment);

        builder.Services.AddControllers(options =>
        {
            options.Filters.Add(new AuthorizeFilter(WorkflowAuthorizationPolicies.Authenticated));
        });

        builder.Services.AddScoped<Backend.API.Validation.Blueprints.IBlueprintVersionValidator>(sp =>
            new Backend.API.Validation.Blueprints.BlueprintVersionValidatorPipeline(
                new Backend.API.Validation.Blueprints.IBlueprintVersionValidator[]
                {
                    ActivatorUtilities.CreateInstance<Backend.API.Validation.Blueprints.ConditionalBranchAuthoringValidator>(sp),
                    ActivatorUtilities.CreateInstance<Backend.API.Validation.Blueprints.BlueprintVersionGraphValidator>(sp),
                    ActivatorUtilities.CreateInstance<Backend.API.Validation.Blueprints.BlueprintVersionSchemaValidator>(sp)
                }));
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();

        builder.Services.AddCors(options =>
        {
            var allowedOrigins = builder.Configuration
                .GetSection(WorkflowClientOriginPolicy.AllowedOriginsSectionName)
                .Get<string[]>()
                ?? [];
            options.AddPolicy(WorkflowClientOriginPolicy.PolicyName, policy =>
            {
                policy.SetIsOriginAllowed(origin => WorkflowClientOriginPolicy.IsAllowed(origin, allowedOrigins))
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

        app.UseCors(WorkflowClientOriginPolicy.PolicyName);
        app.UseAuthentication();
        app.UseAuthorization();

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

}
