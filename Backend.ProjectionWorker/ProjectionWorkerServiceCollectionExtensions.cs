using Backend.Database.Relational;
using Backend.Repositories;
using Backend.Topology;
using Microsoft.Azure.Cosmos;
using Microsoft.EntityFrameworkCore;

namespace Backend.ProjectionWorker;

public static class ProjectionWorkerServiceCollectionExtensions
{
    public static IServiceCollection AddProjectionWorkerServices(
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

        services.AddSingleton<ProjectionWorkerTelemetry>();
        services.AddSingleton<ProcessProjectionChangeHandler>();
        services.AddSingleton<ProcessProjectionRepairService>();
        services.AddHostedService<ProcessProjectionChangeFeedWorker>();

        return services;
    }
}
