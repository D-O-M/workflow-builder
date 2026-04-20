using Backend.Database.State.Objects;
using Backend.Functions.ProgressBroadcaster.Services;
using Backend.Models.Realtime;
using Backend.Repositories.Execution;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.ProgressBroadcaster;

public sealed class ProgressBroadcasterFunction
{
    private readonly SignalRExecutionGateway _signalRGateway;
    private readonly BlueprintDefinitionLookupCache _blueprintDefinitionLookupCache;
    private readonly WorkflowExecutionProjectionService _projectionService;
    private readonly ILogger<ProgressBroadcasterFunction> _logger;

    public ProgressBroadcasterFunction(
        SignalRExecutionGateway signalRGateway,
        BlueprintDefinitionLookupCache blueprintDefinitionLookupCache,
        WorkflowExecutionProjectionService projectionService,
        ILogger<ProgressBroadcasterFunction> logger)
    {
        _signalRGateway = signalRGateway;
        _blueprintDefinitionLookupCache = blueprintDefinitionLookupCache;
        _projectionService = projectionService;
        _logger = logger;
    }

    [Function("ProgressBroadcasterFunction")]
    public async Task Run(
        [CosmosDBTrigger(
            databaseName: "%Realtime:Cosmos:DatabaseName%",
            containerName: "%Realtime:Cosmos:TasksContainerName%",
            Connection = "RealtimeCosmos",
            LeaseContainerName = "%Realtime:Cosmos:LeaseContainerName%",
            CreateLeaseContainerIfNotExists = true)]
        IReadOnlyList<TaskObject> changedTasks,
        CancellationToken cancellationToken)
    {
        if (changedTasks.Count == 0)
        {
            return;
        }

        try
        {
            var subscriptionSnapshot = await _signalRGateway.GetActiveSubscriptionsAsync(cancellationToken);
            if (subscriptionSnapshot.BlueprintDefinitionIds.Count == 0 &&
                subscriptionSnapshot.WorkflowInstanceIds.Count == 0)
            {
                return;
            }

            var changesByWorkflow = GroupChangesByWorkflow(changedTasks);
            if (changesByWorkflow.Count == 0)
            {
                return;
            }

            var blueprintDefinitionIdsByVersionId = await _blueprintDefinitionLookupCache.GetBlueprintDefinitionIdsAsync(
                changesByWorkflow.Values.Select(change => change.BlueprintVersionId),
                cancellationToken);

            var subscribedBlueprintIds = subscriptionSnapshot.BlueprintDefinitionIds.ToHashSet();
            var subscribedWorkflowIds = subscriptionSnapshot.WorkflowInstanceIds.ToHashSet();
            var realtimeBatch = new ExecutionRealtimeBatch();

            foreach (var change in changesByWorkflow.Values)
            {
                if (!blueprintDefinitionIdsByVersionId.TryGetValue(change.BlueprintVersionId, out var blueprintDefinitionId))
                {
                    continue;
                }

                var includeSummary = subscribedBlueprintIds.Contains(blueprintDefinitionId);
                var includeNodeUpdates = subscribedWorkflowIds.Contains(change.WorkflowInstanceId) &&
                    change.ChangedBlueprintTaskIds.Count > 0;

                if (!includeSummary && !includeNodeUpdates)
                {
                    continue;
                }

                try
                {
                    var workflowBatch = await _projectionService.BuildRealtimeBatchAsync(
                        change.BlueprintVersionId,
                        blueprintDefinitionId,
                        change.WorkflowInstanceId,
                        change.ChangedBlueprintTaskIds,
                        includeSummary,
                        includeNodeUpdates,
                        cancellationToken);

                    if (workflowBatch is null)
                    {
                        continue;
                    }

                    realtimeBatch.Summaries.AddRange(workflowBatch.Summaries);
                    realtimeBatch.NodeUpdates.AddRange(workflowBatch.NodeUpdates);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to project realtime updates for workflow {WorkflowInstanceId}.",
                        change.WorkflowInstanceId);
                }
            }

            await _signalRGateway.PublishAsync(realtimeBatch, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast execution progress updates.");
        }
    }

    private static Dictionary<Guid, WorkflowTaskChangeSet> GroupChangesByWorkflow(IReadOnlyList<TaskObject> changedTasks)
    {
        var changesByWorkflow = new Dictionary<Guid, WorkflowTaskChangeSet>();

        foreach (var task in changedTasks)
        {
            if (!Guid.TryParse(task.WorkflowInstanceId, out var workflowInstanceId) ||
                !int.TryParse(task.BlueprintId, out var blueprintVersionId))
            {
                continue;
            }

            if (!changesByWorkflow.TryGetValue(workflowInstanceId, out var changeSet))
            {
                changeSet = new WorkflowTaskChangeSet(workflowInstanceId, blueprintVersionId);
                changesByWorkflow[workflowInstanceId] = changeSet;
            }

            if (Guid.TryParse(task.BlueprintTaskId, out var blueprintTaskId) && blueprintTaskId != Guid.Empty)
            {
                changeSet.ChangedBlueprintTaskIds.Add(blueprintTaskId);
            }
        }

        return changesByWorkflow;
    }

    private sealed class WorkflowTaskChangeSet
    {
        public WorkflowTaskChangeSet(Guid workflowInstanceId, int blueprintVersionId)
        {
            WorkflowInstanceId = workflowInstanceId;
            BlueprintVersionId = blueprintVersionId;
        }

        public Guid WorkflowInstanceId { get; }

        public int BlueprintVersionId { get; }

        public HashSet<Guid> ChangedBlueprintTaskIds { get; } = [];
    }
}
