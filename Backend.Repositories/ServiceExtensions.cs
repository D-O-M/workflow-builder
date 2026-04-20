using Backend.Interfaces.Repositories;
using Backend.Database.State.Objects;
using Backend.Repositories.Processes;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Backend.Repositories;
public static class ServiceExtensions
{
    public static IServiceCollection AddRelationalDataRepositories(this IServiceCollection services)
    {
        services.AddScoped<ITaskDefinitionRepository, TaskDefinitionRepository>();
        services.AddScoped<IBlueprintRepository, BlueprintRepository>();
        services.AddScoped<ProcessProjectionService>();

        return services;
    }

    public static IServiceCollection AddProcessState(
        this IServiceCollection services,
        string databaseName = "enginedb",
        string processesContainerName = "processes",
        string tasksContainerName = "processsteps")
    {
        services.AddSingleton(new ProcessStateInitializationOptions
        {
            DatabaseName = databaseName,
            ProcessesContainerName = processesContainerName,
            TasksContainerName = tasksContainerName
        });
        services.AddHostedService<ProcessStateInitializerHostedService>();

        services.AddSingleton<IProcessStepRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new ProcessStepRepository(client.GetContainer(databaseName, tasksContainerName));
        });

        services.AddSingleton<IProcessInstanceRepository<Dictionary<string, JsonElement>>>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new ProcessInstanceRepository<Dictionary<string, JsonElement>>(
                client.GetContainer(databaseName, processesContainerName));
        });

        return services;
    }
}
