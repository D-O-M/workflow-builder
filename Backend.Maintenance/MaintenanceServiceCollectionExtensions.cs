using Azure.Messaging.ServiceBus;
using Backend.Database.Relational;
using Backend.Engine;
using Backend.Interfaces.Queues;
using Backend.Repositories;
using Backend.Services.ProcessStepQueue;
using Backend.Topology;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;

namespace Backend.Maintenance;

public static class MaintenanceServiceCollectionExtensions
{
    public static IServiceCollection AddMaintenanceInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var sqlConnectionString = LocalDevelopmentResourceSettings.GetTasksDbConnectionString(
            configuration,
            environment);
        services.AddDbContextPool<BlueprintDbContext>(options =>
            options.UseSqlServer(sqlConnectionString));

        var cosmosConnectionString = LocalDevelopmentResourceSettings.GetCosmosConnectionString(
            configuration,
            environment);
        services.AddSingleton(_ => new CosmosClient(
            cosmosConnectionString,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = environment.IsDevelopment(),
                RequestTimeout = TimeSpan.FromSeconds(10),
                UseSystemTextJsonSerializerWithOptions = CosmosSerialization.CreateJsonOptions()
            }));

        services.AddRelationalDataRepositories();
        services.AddProcessState(
            databaseName: configuration[WorkflowBuilderTopology.Cosmos.DatabaseNameKey]!);

        services.AddSingleton(_ => new ServiceBusClient(
            LocalDevelopmentResourceSettings.GetProcessStepQueueConnectionString(
                configuration,
                environment)));
        services.AddSingleton(provider =>
        {
            var client = provider.GetRequiredService<ServiceBusClient>();
            return client.CreateSender(LocalDevelopmentResourceSettings.GetProcessStepQueueName(
                configuration,
                environment));
        });
        services.AddSingleton<IProcessStepQueueWriter, ServiceBusProcessStepQueueWriter>();

        return services;
    }

    public static IServiceCollection AddMaintenanceWorkers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(_ =>
            configuration
                .GetSection("ProcessStepDispatch")
                .Get<ProcessStepDispatchOptions>()
            ?? new ProcessStepDispatchOptions());
        services.AddSingleton(_ =>
            configuration
                .GetSection("FinalizationRecovery")
                .Get<StepFinalizationRecoveryOptions>()
            ?? new StepFinalizationRecoveryOptions());
        services.AddSingleton(_ =>
            configuration
                .GetSection("ProcessStartReconciliation")
                .Get<ProcessStartReconciliationOptions>()
            ?? new ProcessStartReconciliationOptions());

        services.AddSingleton<ProcessStepDispatchTelemetry>();
        services.AddSingleton<ProcessStepDispatchService>();
        services.AddSingleton<IProcessStepDispatcher>(provider =>
            provider.GetRequiredService<ProcessStepDispatchService>());

        services.AddSingleton<ActivationPlanner>();
        services.AddTransient<ActivationPersistenceService>();
        services.AddTransient<StepFinalizationService>();
        services.AddTransient<IStepFinalizationService>(provider =>
            provider.GetRequiredService<StepFinalizationService>());
        services.AddSingleton<StepClaimService>();
        services.AddSingleton<Backend.Engine.ProcessLifecycleTelemetry>();

        services.AddSingleton<StepFinalizationRecoveryTelemetry>();
        services.AddSingleton<ProcessStartReconciliationTelemetry>();
        services.AddTransient<StepFinalizationRecoveryService>();
        services.AddTransient<ProcessStartReconciliationService>();
        services.AddHostedService<StepFinalizationRecoveryWorker>();
        services.AddHostedService<ProcessStartReconciliationWorker>();

        return services;
    }
}
