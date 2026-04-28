namespace Backend.Repositories;

public sealed class ProcessStateInitializationOptions
{
    public string DatabaseName { get; init; } = "enginedb";

    public string ProcessesContainerName { get; init; } = "processes";

    public string TasksContainerName { get; init; } = "processsteps";

    public string ActivationsContainerName { get; init; } = "nodeactivations";

    public string JoinStatesContainerName { get; init; } = "nodejoinstates";
}
