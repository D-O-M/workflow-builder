using Azure.Messaging.ServiceBus;
using Backend.Database.Relational;
using Backend.API.Services;
using Backend.Interfaces.Repositories;
using Backend.Repositories;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

namespace Backend.API;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();

        // Add services to the container.
        builder.Services.AddDbContextPool<WorkflowDbContext>(options =>
        {
            options.UseSqlServer(builder.Configuration.GetConnectionString("SqlServer"));
        });

        var cosmosConnectionName = builder.Configuration["AzureCosmos:ConnectionName"]
            ?? throw new InvalidOperationException("Azure Cosmos connection name is not configured.");
        var workflowTaskQueueConnectionName = builder.Configuration["AzureServiceBus:Queues:WorkflowTaskQueue:ConnectionName"]
            ?? throw new InvalidOperationException("Workflow task queue connection name is not configured.");

        builder.AddAzureCosmosClient(cosmosConnectionName);
        builder.Services.AddRelationalDataRepositories();
        builder.Services.AddWorkflowExecutionState();

        var serviceBusConnectionString = builder.Configuration.GetConnectionString(workflowTaskQueueConnectionName)
            ?? throw new InvalidOperationException("Workflow task queue connection string is not configured.");

        var workflowTaskQueueName = builder.Configuration["AzureServiceBus:Queues:WorkflowTaskQueue:Name"]
            ?? throw new InvalidOperationException("Workflow task queue name is not configured.");

        builder.Services.AddSingleton(_ => new ServiceBusClient(serviceBusConnectionString));
        builder.Services.AddSingleton(provider =>
        {
            var client = provider.GetRequiredService<ServiceBusClient>();
            return client.CreateSender(workflowTaskQueueName);
        });
        builder.Services.AddScoped<BlueprintExecutionService>();

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
