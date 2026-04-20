using Backend.Database.State.Objects;
using Backend.Functions.ProgressBroadcaster.Services;
using Backend.Models.Realtime;
using Backend.Repositories.Processes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Backend.Functions.ProgressBroadcaster;

public sealed class ProgressBroadcasterFunction
{
    private readonly SignalRProcessGateway _signalRGateway;
    private readonly BlueprintDefinitionLookupCache _blueprintDefinitionLookupCache;
    private readonly ProcessProjectionService _projectionService;
    private readonly ILogger<ProgressBroadcasterFunction> _logger;

    public ProgressBroadcasterFunction(
        SignalRProcessGateway signalRGateway,
        BlueprintDefinitionLookupCache blueprintDefinitionLookupCache,
        ProcessProjectionService projectionService,
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
        IReadOnlyList<ProcessStepRecord> changedTasks,
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
                subscriptionSnapshot.ProcessIds.Count == 0)
            {
                return;
            }

            var changesByProcess = GroupChangesByProcess(changedTasks);
            if (changesByProcess.Count == 0)
            {
                return;
            }

            var blueprintDefinitionIdsByVersionId = await _blueprintDefinitionLookupCache.GetBlueprintDefinitionIdsAsync(
                changesByProcess.Values.Select(change => change.BlueprintVersionId),
                cancellationToken);

            var subscribedBlueprintIds = subscriptionSnapshot.BlueprintDefinitionIds.ToHashSet();
            var subscribedProcessIds = subscriptionSnapshot.ProcessIds.ToHashSet();
            var realtimeBatch = new ProcessRealtimeBatch();

            foreach (var change in changesByProcess.Values)
            {
                if (!blueprintDefinitionIdsByVersionId.TryGetValue(change.BlueprintVersionId, out var blueprintDefinitionId))
                {
                    continue;
                }

                var includeSummary = subscribedBlueprintIds.Contains(blueprintDefinitionId);
                var includeNodeUpdates = subscribedProcessIds.Contains(change.ProcessId) &&
                    change.ChangedBlueprintNodeIds.Count > 0;

                if (!includeSummary && !includeNodeUpdates)
                {
                    continue;
                }

                try
                {
                    var processBatch = await _projectionService.BuildRealtimeBatchAsync(
                        change.BlueprintVersionId,
                        blueprintDefinitionId,
                        change.ProcessId,
                        change.ChangedBlueprintNodeIds,
                        includeSummary,
                        includeNodeUpdates,
                        cancellationToken);

                    if (processBatch is null)
                    {
                        continue;
                    }

                    realtimeBatch.Summaries.AddRange(processBatch.Summaries);
                    realtimeBatch.NodeUpdates.AddRange(processBatch.NodeUpdates);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to project realtime updates for process {ProcessId}.",
                        change.ProcessId);
                }
            }

            await _signalRGateway.PublishAsync(realtimeBatch, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast process progress updates.");
        }
    }

    private static Dictionary<Guid, ProcessStepChangeSet> GroupChangesByProcess(IReadOnlyList<ProcessStepRecord> changedTasks)
    {
        var changesByProcess = new Dictionary<Guid, ProcessStepChangeSet>();

        foreach (var task in changedTasks)
        {
            if (!Guid.TryParse(task.ProcessId, out var processId) ||
                !int.TryParse(task.BlueprintId, out var blueprintVersionId))
            {
                continue;
            }

            if (!changesByProcess.TryGetValue(processId, out var changeSet))
            {
                changeSet = new ProcessStepChangeSet(processId, blueprintVersionId);
                changesByProcess[processId] = changeSet;
            }

            if (Guid.TryParse(task.BlueprintNodeId, out var blueprintNodeId) && blueprintNodeId != Guid.Empty)
            {
                changeSet.ChangedBlueprintNodeIds.Add(blueprintNodeId);
            }
        }

        return changesByProcess;
    }

    private sealed class ProcessStepChangeSet
    {
        public ProcessStepChangeSet(Guid processId, int blueprintVersionId)
        {
            ProcessId = processId;
            BlueprintVersionId = blueprintVersionId;
        }

        public Guid ProcessId { get; }

        public int BlueprintVersionId { get; }

        public HashSet<Guid> ChangedBlueprintNodeIds { get; } = [];
    }
}
