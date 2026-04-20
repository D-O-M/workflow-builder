namespace Backend.Repositories;

public sealed class WorkflowExecutionStateInitializationOptions
{
    public string DatabaseName { get; init; } = "enginedb";

    public string WorkflowsContainerName { get; init; } = "workflows";

    public string TasksContainerName { get; init; } = "tasks";
}
