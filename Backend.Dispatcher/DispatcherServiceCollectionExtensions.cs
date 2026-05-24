using Azure.Messaging.ServiceBus;
using Backend.Interfaces.Queues;
using Backend.Repositories;
using Backend.Services.ProcessStepQueue;
using Backend.Topology;
using Microsoft.Azure.Cosmos;

namespace Backend.Dispatcher;

public static class DispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddDispatcherInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
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

    public static IServiceCollection AddDispatcherWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(_ =>
            configuration
                .GetSection("ProcessStepDispatch")
                .Get<ProcessStepDispatchOptions>()
            ?? new ProcessStepDispatchOptions());
        services.AddSingleton<ProcessStepDispatchTelemetry>();
        services.AddSingleton<ProcessStepDispatchService>();
        services.AddSingleton<IProcessStepDispatcher>(provider =>
            provider.GetRequiredService<ProcessStepDispatchService>());
        services.AddHostedService<ProcessStepDispatchWorker>();

        return services;
    }
}
