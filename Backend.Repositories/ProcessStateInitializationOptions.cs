using Backend.Topology;

namespace Backend.Repositories;

public sealed class ProcessStateInitializationOptions
{
    public string DatabaseName { get; init; } = WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;

    public string ProcessesContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.Processes.Name;

    public string ProcessDirectoryContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.ProcessDirectory.Name;

    public string ProcessSummariesContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.ProcessSummaries.Name;

    public string ProcessRealtimeSummariesContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.ProcessRealtimeSummaries.Name;

    public string ProcessNodeRealtimeContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.ProcessNodeRealtime.Name;

    public string TasksContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name;

    public string StepDispatchesContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.ProcessStepDispatches.Name;

    public string StepFinalizationsContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.ProcessStepFinalizations.Name;

    public string ActivationsContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.NodeActivations.Name;

    public string JoinStatesContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.NodeJoinStates.Name;

    public string PhasedProcessesContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.PhasedProcesses.Name;

    public string PhaseCorrelationsContainerName { get; init; } = WorkflowBuilderTopology.Cosmos.Containers.PhaseCorrelations.Name;
}
