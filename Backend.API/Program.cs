using Azure.Messaging.ServiceBus;
using Backend.Database.Relational;
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
        builder.Services.AddScoped<WorkflowExecutionQueryService>();

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
                policy.WithOrigins("http://localhost:4200") // Angular dev server
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials(); // optional, if you're using cookies/auth
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
}
