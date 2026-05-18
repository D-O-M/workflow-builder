using Backend.Interfaces.Repositories;
using Backend.Database.State.Objects;
using Backend.Repositories.Processes;
using Backend.Topology;
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
        string? databaseName = null,
        string? processesContainerName = null,
        string? processDirectoryContainerName = null,
        string? processSummariesContainerName = null,
        string? tasksContainerName = null,
        string? stepFinalizationsContainerName = null,
        string? activationsContainerName = null,
        string? joinStatesContainerName = null,
        string? phasedProcessesContainerName = null,
        string? phaseCorrelationsContainerName = null)
    {
        databaseName ??= WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;
        processesContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.Processes.Name;
        processDirectoryContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.ProcessDirectory.Name;
        processSummariesContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.ProcessSummaries.Name;
        tasksContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name;
        stepFinalizationsContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.ProcessStepFinalizations.Name;
        activationsContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.NodeActivations.Name;
        joinStatesContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.NodeJoinStates.Name;
        phasedProcessesContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.PhasedProcesses.Name;
        phaseCorrelationsContainerName ??= WorkflowBuilderTopology.Cosmos.Containers.PhaseCorrelations.Name;

        services.AddSingleton(new ProcessStateInitializationOptions
        {
            DatabaseName = databaseName,
            ProcessesContainerName = processesContainerName,
            ProcessDirectoryContainerName = processDirectoryContainerName,
            ProcessSummariesContainerName = processSummariesContainerName,
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

        services.AddSingleton<IProcessDirectoryRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new ProcessDirectoryRepository(
                client.GetContainer(databaseName, processDirectoryContainerName));
        });

        services.AddSingleton<IProcessSummaryProjectionRepository>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return new ProcessSummaryProjectionRepository(
                client.GetContainer(databaseName, processSummariesContainerName));
        });

        services.AddSingleton<IProcessIndexProjectionService, ProcessIndexProjectionService>();

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
