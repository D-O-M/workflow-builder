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
        services.AddMemoryCache();
        services.AddScoped<ITaskDefinitionRepository, TaskDefinitionRepository>();
        services.AddScoped<IBlueprintRepository, BlueprintRepository>();
        services.AddScoped<IPhasedBlueprintRepository, PhasedBlueprintRepository>();
        services.AddSingleton<IBlueprintExecutionCache, BlueprintExecutionCache>();
        services.AddScoped<ProcessProjectionService>();
        services.AddScoped<PhasedProcessFinalizationService>();

        return services;
    }

    public static IServiceCollection AddProcessState(
        this IServiceCollection services,
        string databaseName = "enginedb",
        string processesContainerName = "processes",
        string tasksContainerName = "processsteps",
        string stepFinalizationsContainerName = "processstepfinalizations",
        string activationsContainerName = "nodeactivations",
        string joinStatesContainerName = "nodejoinstates",
        string phasedProcessesContainerName = "phasedprocesses",
        string phaseCorrelationsContainerName = "phasecorrelations")
    {
        services.AddSingleton(new ProcessStateInitializationOptions
        {
            DatabaseName = databaseName,
            ProcessesContainerName = processesContainerName,
            TasksContainerName = tasksContainerName,
            StepFinalizationsContainerName = stepFinalizationsContainerName,
            ActivationsContainerName = activationsContainerName,
            JoinStatesContainerName = joinStatesContainerName,
            PhasedProcessesContainerName = phasedProcessesContainerName,
            PhaseCorrelationsContainerName = phaseCorrelationsContainerName
        });

        services.AddSingleton<IProcessStepRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new ProcessStepRepository(client.GetContainer(databaseName, tasksContainerName));
        });

        services.AddSingleton<IProcessStepFinalizationRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new ProcessStepFinalizationRepository(
                client.GetContainer(databaseName, stepFinalizationsContainerName));
        });

        services.AddSingleton<INodeActivationRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new NodeActivationRepository(client.GetContainer(databaseName, activationsContainerName));
        });

        services.AddSingleton<INodeJoinStateRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new NodeJoinStateRepository(client.GetContainer(databaseName, joinStatesContainerName));
        });

        services.AddSingleton<IProcessInstanceRepository<Dictionary<string, JsonElement>>>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new ProcessInstanceRepository<Dictionary<string, JsonElement>>(
                client.GetContainer(databaseName, processesContainerName));
        });

        services.AddSingleton<IPhasedProcessRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new PhasedProcessRepository(client.GetContainer(databaseName, phasedProcessesContainerName));
        });

        services.AddSingleton<IPhaseCorrelationRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new PhaseCorrelationRepository(client.GetContainer(databaseName, phaseCorrelationsContainerName));
        });

        return services;
    }
}
