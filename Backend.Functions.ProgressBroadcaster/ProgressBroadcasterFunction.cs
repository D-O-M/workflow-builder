using Backend.Database.State.Objects;
using Backend.Functions.ProgressBroadcaster.Services;
using Microsoft.Azure.Functions.Worker;

namespace Backend.Functions.ProgressBroadcaster;

public sealed class ProgressBroadcasterFunction
{
    private readonly ProcessProgressBroadcaster _broadcaster;

    public ProgressBroadcasterFunction(ProcessProgressBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    [Function("ProgressBroadcasterFunction")]
    public Task Run(
        [CosmosDBTrigger(
            databaseName: "%Realtime:Cosmos:DatabaseName%",
            containerName: "%Realtime:Cosmos:TasksContainerName%",
            Connection = "RealtimeCosmos",
            LeaseContainerName = "%Realtime:Cosmos:LeaseContainerName%",
            CreateLeaseContainerIfNotExists = true)]
        IReadOnlyList<ProcessStepRecord> changedTasks,
        CancellationToken cancellationToken) =>
        _broadcaster.BroadcastAsync(changedTasks, cancellationToken);
}
