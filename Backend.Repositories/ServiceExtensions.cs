using Backend.Interfaces.Repositories;
using Backend.Database.State.Objects;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Backend.Repositories;
public static class ServiceExtensions
{
    public static IServiceCollection AddRelationalDataRepositories(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowTaskRepository, WorkflowTaskRepository>();
        services.AddScoped<IWorkflowBlueprintRepository, WorkflowBlueprintRepository>();

        return services;
    }

    public static IServiceCollection AddWorkflowExecutionState(
        this IServiceCollection services,
        string databaseName = "enginedb",
        string workflowsContainerName = "workflows",
        string tasksContainerName = "tasks")
    {
        services.AddSingleton<ITaskInstanceRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new TaskInstanceRepository(client.GetContainer(databaseName, tasksContainerName));
        });

        services.AddSingleton<IWorkflowInstanceRepository<Dictionary<string, JsonElement>>>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new WorkflowInstanceRepository<Dictionary<string, JsonElement>>(
                client.GetContainer(databaseName, workflowsContainerName));
        });

        return services;
    }
}
