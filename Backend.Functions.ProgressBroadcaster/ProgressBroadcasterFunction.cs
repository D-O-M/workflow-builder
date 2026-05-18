using Backend.Database.State.Objects;
using Backend.Functions.ProgressBroadcaster.Services;
using Backend.Topology;
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
            databaseName: WorkflowBuilderTopology.Realtime.CosmosDatabaseNameBindingExpression,
            containerName: WorkflowBuilderTopology.Realtime.ProcessStepsContainerNameBindingExpression,
            Connection = WorkflowBuilderTopology.Realtime.RealtimeCosmosConnectionName,
            LeaseContainerName = WorkflowBuilderTopology.Realtime.LeaseContainerNameBindingExpression,
            CreateLeaseContainerIfNotExists = true)]
        IReadOnlyList<ProcessStepRecord> changedTasks,
        CancellationToken cancellationToken) =>
        _broadcaster.BroadcastAsync(changedTasks, cancellationToken);
}
