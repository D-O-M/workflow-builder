using System.Text.Json;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.Realtime;
using Backend.Models.Versioning;
using Backend.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Backend.Repositories.Execution;

public sealed class WorkflowExecutionProjectionService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly ITaskInstanceRepository _taskInstanceRepository;
    private readonly IWorkflowInstanceRepository<Dictionary<string, JsonElement>> _workflowInstanceRepository;

    public WorkflowExecutionProjectionService(
        WorkflowDbContext dbContext,
        ITaskInstanceRepository taskInstanceRepository,
        IWorkflowInstanceRepository<Dictionary<string, JsonElement>> workflowInstanceRepository)
    {
        _dbContext = dbContext;
        _taskInstanceRepository = taskInstanceRepository;
        _workflowInstanceRepository = workflowInstanceRepository;
    }

    public async Task<List<WorkflowExecutionSummaryViewModel>> GetExecutionsForBlueprintDefinitionAsync(
        int blueprintDefinitionId,
        CancellationToken cancellationToken)
    {
        var definition = await _dbContext.BlueprintDefinitions
            .Include(d => d.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == blueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(blueprintDefinitionId);
        }

        var summaries = new List<WorkflowExecutionSummaryViewModel>();
        foreach (var version in definition.Versions.OrderByDescending(v => v.Version))
        {
            var workflows = await _workflowInstanceRepository.GetWorkflowStatesForBlueprint(
                version.Id,
                cancellationToken);

            foreach (var workflow in workflows)
            {
                var tasks = await _taskInstanceRepository.GetTaskInstancesForWorkflow(
                    Guid.Parse(workflow.WorkflowInstanceId),
                    cancellationToken);

                summaries.Add(BuildSummary(definition.Id, version, workflow, tasks));
            }
        }

        return summaries
            .OrderByDescending(summary => summary.CreatedAt)
            .ToList();
    }

    public async Task<WorkflowExecutionDetailViewModel> GetExecutionDetailAsync(
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var context = await LoadExecutionContextAsync(
            blueprintDefinitionId,
            workflowInstanceId,
            cancellationToken);

        return BuildDetail(
            context.Definition,
            context.BlueprintVersion,
            context.Workflow,
            context.ExecutionTasks);
    }

    public async Task<WorkflowExecutionStepDetailViewModel> GetExecutionStepDetailAsync(
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        Guid blueprintTaskInstanceId,
        CancellationToken cancellationToken)
    {
        var context = await LoadExecutionContextAsync(
            blueprintDefinitionId,
            workflowInstanceId,
            cancellationToken);
        var taskInstance = context.BlueprintVersion.TaskInstances
            .FirstOrDefault(candidate => candidate.Id == blueprintTaskInstanceId)
            ?? throw new EntityNotFoundException<WorkflowExecutionStepDetailViewModel>(blueprintTaskInstanceId.ToString());
        context.ExecutionTasksByBlueprintTaskId.TryGetValue(taskInstance.Id, out var taskState);
        var actualResolvedVersion = ResolveActualVersion(taskInstance, taskState);
        var endedAt = taskState?.FailedAt ?? taskState?.CompletedAt;

        return new WorkflowExecutionStepDetailViewModel
        {
            BlueprintTaskInstanceId = taskInstance.Id,
            Name = taskInstance.TaskDefinition.Name,
            Status = taskState is not null
                ? MapTaskStatus(taskState.Status)
                : "NotExecuted",
            ResolvedVersion = taskState?.ResolvedTaskVersion ?? actualResolvedVersion.Version.ToString(),
            StartedAt = taskState?.StartedAt,
            EndedAt = endedAt,
            DurationMs = CalculateDurationMs(taskState?.StartedAt, endedAt),
            FailureMessage = taskState?.ErrorMessage,
            Inputs = ResolveStepInputs(taskInstance, actualResolvedVersion, context),
            Outputs = SerializePayloads(taskState?.OutputData),
        };
    }

    public async Task<Guid?> GetRuntimeTaskInstanceIdAsync(
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        Guid blueprintTaskInstanceId,
        CancellationToken cancellationToken)
    {
        var context = await LoadExecutionContextAsync(
            blueprintDefinitionId,
            workflowInstanceId,
            cancellationToken);
        var taskInstance = context.BlueprintVersion.TaskInstances
            .FirstOrDefault(candidate => candidate.Id == blueprintTaskInstanceId)
            ?? throw new EntityNotFoundException<WorkflowExecutionStepDetailViewModel>(blueprintTaskInstanceId.ToString());

        if (!context.ExecutionTasksByBlueprintTaskId.TryGetValue(taskInstance.Id, out var taskState))
        {
            return null;
        }

        return Guid.TryParse(taskState.Id, out var runtimeTaskInstanceId)
            ? runtimeTaskInstanceId
            : null;
    }

    public async Task<ExecutionRealtimeBatch?> BuildRealtimeBatchAsync(
        int blueprintVersionId,
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        IReadOnlyCollection<Guid> changedBlueprintTaskIds,
        bool includeSummary,
        bool includeNodeUpdates,
        CancellationToken cancellationToken)
    {
        if (!includeSummary && (!includeNodeUpdates || changedBlueprintTaskIds.Count == 0))
        {
            return null;
        }

        var workflow = await _workflowInstanceRepository.GetWorkflowStateById(
            workflowInstanceId,
            blueprintVersionId,
            cancellationToken);
        var executionTasks = await _taskInstanceRepository.GetTaskInstancesForWorkflow(
            workflowInstanceId,
            cancellationToken);

        var versionMetadata = await _dbContext.BlueprintVersions
            .AsNoTracking()
            .Where(version => version.Id == blueprintVersionId)
            .Select(version => new
            {
                version.Id,
                version.Version,
                version.Name,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (versionMetadata is null)
        {
            throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersionId);
        }

        var batch = new ExecutionRealtimeBatch();

        if (includeSummary)
        {
            batch.Summaries.Add(BuildSummaryEvent(
                blueprintDefinitionId,
                versionMetadata.Id,
                versionMetadata.Version,
                versionMetadata.Name,
                workflow,
                executionTasks));
        }

        if (includeNodeUpdates && changedBlueprintTaskIds.Count > 0)
        {
            var deltaTasks = await LoadBlueprintTaskInstancesForDeltasAsync(
                blueprintVersionId,
                changedBlueprintTaskIds,
                cancellationToken);

            if (deltaTasks.Count > 0)
            {
                var executionTasksByBlueprintTaskId = executionTasks
                    .Where(task => Guid.TryParse(task.BlueprintTaskId, out var parsedId) && parsedId != Guid.Empty)
                    .ToDictionary(task => Guid.Parse(task.BlueprintTaskId), task => task);

                var nodeDeltas = deltaTasks
                    .Select(taskInstance => BuildNodeDelta(taskInstance, executionTasksByBlueprintTaskId))
                    .Where(delta => delta is not null)
                    .Cast<ExecutionNodeDelta>()
                    .ToList();

                if (nodeDeltas.Count > 0)
                {
                    batch.NodeUpdates.Add(new ExecutionNodesUpdatedEvent
                    {
                        WorkflowInstanceId = workflowInstanceId,
                        BlueprintDefinitionId = blueprintDefinitionId,
                        BlueprintVersionId = blueprintVersionId,
                        Nodes = nodeDeltas,
                    });
                }
            }
        }

        return batch.Summaries.Count == 0 && batch.NodeUpdates.Count == 0
            ? null
            : batch;
    }

    private async Task<BlueprintVersion?> LoadExecutionBlueprintGraphAsync(
        int blueprintVersionId,
        CancellationToken cancellationToken) =>
        await _dbContext.BlueprintVersions
            .Include(version => version.TaskInstances)
                .ThenInclude(taskInstance => taskInstance.TaskDefinition)
                    .ThenInclude(taskDefinition => taskDefinition.Versions)
                        .ThenInclude(taskVersion => taskVersion.Channels)
            .Include(version => version.TaskInstances)
                .ThenInclude(taskInstance => taskInstance.OutgoingConnections)
            .Include(version => version.TaskInstances)
                .ThenInclude(taskInstance => taskInstance.IncomingConnections)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(version => version.Id == blueprintVersionId, cancellationToken);

    private async Task<List<TaskInstance>> LoadBlueprintTaskInstancesForDeltasAsync(
        int blueprintVersionId,
        IReadOnlyCollection<Guid> changedBlueprintTaskIds,
        CancellationToken cancellationToken)
    {
        if (changedBlueprintTaskIds.Count == 0)
        {
            return [];
        }

        return await _dbContext.TaskInstances
            .Where(taskInstance =>
                taskInstance.BlueprintVersionId == blueprintVersionId &&
                changedBlueprintTaskIds.Contains(taskInstance.Id))
            .Include(taskInstance => taskInstance.TaskDefinition)
                .ThenInclude(taskDefinition => taskDefinition.Versions)
                    .ThenInclude(taskVersion => taskVersion.Channels)
            .AsNoTracking()
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
    }

    private static WorkflowExecutionDetailViewModel BuildDetail(
        BlueprintDefinition definition,
        BlueprintVersion blueprintVersionEntity,
        WorkflowObject<Dictionary<string, JsonElement>> workflow,
        IReadOnlyCollection<TaskObject> executionTasks)
    {
        var executionTasksByBlueprintTaskId = executionTasks
            .Where(task => Guid.TryParse(task.BlueprintTaskId, out var parsedId) && parsedId != Guid.Empty)
            .ToDictionary(task => Guid.Parse(task.BlueprintTaskId), task => task);

        var nodes = blueprintVersionEntity.TaskInstances
            .OrderBy(taskInstance => taskInstance.PositionY)
            .ThenBy(taskInstance => taskInstance.PositionX)
            .Select(taskInstance => BuildNodeViewModel(taskInstance, executionTasksByBlueprintTaskId))
            .ToList();

        return new WorkflowExecutionDetailViewModel
        {
            WorkflowInstanceId = Guid.Parse(workflow.WorkflowInstanceId),
            BlueprintDefinitionId = definition.Id,
            BlueprintName = definition.DisplayName,
            BlueprintVersionId = blueprintVersionEntity.Id,
            BlueprintVersionNumber = blueprintVersionEntity.Version,
            BlueprintVersionName = blueprintVersionEntity.Name,
            Status = NormalizeWorkflowStatus(workflow.Status, executionTasks),
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt,
            StartingTaskInstanceId = blueprintVersionEntity.StartingTaskInstanceId ?? Guid.Empty,
            Nodes = nodes,
        };
    }

    private async Task<ExecutionProjectionContext> LoadExecutionContextAsync(
        int blueprintDefinitionId,
        Guid workflowInstanceId,
        CancellationToken cancellationToken)
    {
        var definition = await _dbContext.BlueprintDefinitions
            .Include(candidate => candidate.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == blueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(blueprintDefinitionId);
        }

        WorkflowObject<Dictionary<string, JsonElement>>? workflow = null;
        BlueprintVersion? blueprintVersion = null;

        foreach (var version in definition.Versions.OrderByDescending(candidate => candidate.Version))
        {
            try
            {
                workflow = await _workflowInstanceRepository.GetWorkflowStateById(
                    workflowInstanceId,
                    version.Id,
                    cancellationToken);
                blueprintVersion = version;
                break;
            }
            catch (EntityNotFoundException<WorkflowObject<Dictionary<string, JsonElement>>>)
            {
                // Continue searching across blueprint versions.
            }
        }

        if (workflow is null || blueprintVersion is null)
        {
            throw new EntityNotFoundException<WorkflowExecutionDetailViewModel>(workflowInstanceId.ToString());
        }

        var blueprintVersionEntity = await LoadExecutionBlueprintGraphAsync(blueprintVersion.Id, cancellationToken)
            ?? throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersion.Id);
        var executionTasks = await _taskInstanceRepository.GetTaskInstancesForWorkflow(
            workflowInstanceId,
            cancellationToken);
        var executionTasksByBlueprintTaskId = executionTasks
            .Where(task => Guid.TryParse(task.BlueprintTaskId, out var parsedId) && parsedId != Guid.Empty)
            .ToDictionary(task => Guid.Parse(task.BlueprintTaskId), task => task);

        return new ExecutionProjectionContext(
            definition,
            blueprintVersionEntity,
            workflow,
            executionTasks,
            executionTasksByBlueprintTaskId);
    }

    private static JsonElement? ResolveStepInputs(
        TaskInstance taskInstance,
        TaskVersion resolvedVersion,
        ExecutionProjectionContext context)
    {
        var inputChannelName = resolvedVersion.InputChannels.SingleOrDefault()?.Name;
        var incomingConnection = taskInstance.IncomingConnections
            .FirstOrDefault(connection => connection.SourceTaskInstanceId.HasValue);

        if (incomingConnection is not null)
        {
            if (!context.ExecutionTasksByBlueprintTaskId.TryGetValue(incomingConnection.SourceTaskInstanceId!.Value, out var sourceTaskState))
            {
                return null;
            }

            return TryBuildNamedPayload(
                sourceTaskState.OutputData,
                incomingConnection.ChannelName,
                inputChannelName ?? incomingConnection.ChannelName);
        }

        var seedTask = context.ExecutionTasks
            .FirstOrDefault(task => string.Equals(task.BlueprintTaskId, Guid.Empty.ToString(), StringComparison.OrdinalIgnoreCase));

        return seedTask is null || string.IsNullOrWhiteSpace(inputChannelName)
            ? null
            : TryBuildNamedPayload(seedTask.OutputData, inputChannelName, inputChannelName);
    }

    private static JsonElement? TryBuildNamedPayload(
        IReadOnlyDictionary<string, JsonElement>? payloads,
        string payloadKey,
        string propertyName)
    {
        if (payloads is null || !payloads.TryGetValue(payloadKey, out var payload))
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
        {
            [propertyName] = payload,
        });
    }

    private static JsonElement? SerializePayloads(IReadOnlyDictionary<string, JsonElement>? payloads)
    {
        if (payloads is null || payloads.Count == 0)
        {
            return null;
        }

        return JsonSerializer.SerializeToElement(payloads);
    }

    private static double? CalculateDurationMs(DateTimeOffset? startedAt, DateTimeOffset? endedAt)
    {
        if (!startedAt.HasValue || !endedAt.HasValue)
        {
            return null;
        }

        return Math.Max(0, (endedAt.Value - startedAt.Value).TotalMilliseconds);
    }

    private static WorkflowExecutionSummaryViewModel BuildSummary(
        int blueprintDefinitionId,
        BlueprintVersion blueprintVersion,
        WorkflowObject<Dictionary<string, JsonElement>> workflow,
        IReadOnlyCollection<TaskObject> executionTasks) =>
        BuildSummary(
            blueprintDefinitionId,
            blueprintVersion.Id,
            blueprintVersion.Version,
            blueprintVersion.Name,
            workflow,
            executionTasks);

    private static WorkflowExecutionSummaryViewModel BuildSummary(
        int blueprintDefinitionId,
        int blueprintVersionId,
        int blueprintVersionNumber,
        string blueprintVersionName,
        WorkflowObject<Dictionary<string, JsonElement>> workflow,
        IReadOnlyCollection<TaskObject> executionTasks) =>
        new()
        {
            WorkflowInstanceId = Guid.Parse(workflow.WorkflowInstanceId),
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintVersionId = blueprintVersionId,
            BlueprintVersionNumber = blueprintVersionNumber,
            BlueprintVersionName = blueprintVersionName,
            Status = NormalizeWorkflowStatus(workflow.Status, executionTasks),
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt,
            CompletedTaskCount = executionTasks.Count(task => task.Status == TaskExecutionStatus.Completed),
            FailedTaskCount = executionTasks.Count(task => task.Status == TaskExecutionStatus.Failed),
            InProgressTaskCount = executionTasks.Count(task => task.Status == TaskExecutionStatus.InProgress),
            PendingTaskCount = executionTasks.Count(task => task.Status == TaskExecutionStatus.Pending),
        };

    private static ExecutionSummaryUpdatedEvent BuildSummaryEvent(
        int blueprintDefinitionId,
        int blueprintVersionId,
        int blueprintVersionNumber,
        string blueprintVersionName,
        WorkflowObject<Dictionary<string, JsonElement>> workflow,
        IReadOnlyCollection<TaskObject> executionTasks) =>
        new()
        {
            WorkflowInstanceId = Guid.Parse(workflow.WorkflowInstanceId),
            BlueprintDefinitionId = blueprintDefinitionId,
            BlueprintVersionId = blueprintVersionId,
            BlueprintVersionNumber = blueprintVersionNumber,
            BlueprintVersionName = blueprintVersionName,
            Status = NormalizeWorkflowStatus(workflow.Status, executionTasks),
            CreatedAt = workflow.CreatedAt,
            UpdatedAt = workflow.UpdatedAt,
            CompletedTaskCount = executionTasks.Count(task => task.Status == TaskExecutionStatus.Completed),
            FailedTaskCount = executionTasks.Count(task => task.Status == TaskExecutionStatus.Failed),
            InProgressTaskCount = executionTasks.Count(task => task.Status == TaskExecutionStatus.InProgress),
            PendingTaskCount = executionTasks.Count(task => task.Status == TaskExecutionStatus.Pending),
        };

    private static WorkflowExecutionNodeViewModel BuildNodeViewModel(
        TaskInstance taskInstance,
        IReadOnlyDictionary<Guid, TaskObject> executionTasksByBlueprintTaskId)
    {
        executionTasksByBlueprintTaskId.TryGetValue(taskInstance.Id, out var taskState);
        var actualResolvedVersion = ResolveActualVersion(taskInstance, taskState);

        return new WorkflowExecutionNodeViewModel
        {
            InstanceId = taskInstance.Id,
            RuntimeTaskInstanceId = taskState is not null && Guid.TryParse(taskState.Id, out var taskInstanceId)
                ? taskInstanceId
                : null,
            TaskDefinitionId = taskInstance.TaskDefinitionId,
            AllowedMajor = taskInstance.AllowedMajor,
            AllowedMinor = taskInstance.AllowedMinor,
            ResolvedVersionId = taskState?.ResolvedTaskVersionId ?? actualResolvedVersion.Id,
            ResolvedVersion = taskState?.ResolvedTaskVersion ?? actualResolvedVersion.Version.ToString(),
            Name = taskInstance.TaskDefinition.Name,
            Status = taskState is not null
                ? MapTaskStatus(taskState.Status)
                : "NotExecuted",
            StartedAt = taskState?.StartedAt,
            CompletedAt = taskState?.CompletedAt,
            FailedAt = taskState?.FailedAt,
            FailureMessage = taskState?.ErrorMessage,
            Position = new CoordinatesViewModel
            {
                X = taskInstance.PositionX,
                Y = taskInstance.PositionY,
            },
            InputChannelName = actualResolvedVersion.InputChannels.SingleOrDefault()?.Name,
            OutputChannelNames = actualResolvedVersion.OutputChannels
                .OrderBy(channel => channel.Id)
                .Select(channel => channel.Name)
                .ToList(),
            OutgoingConnections = taskInstance.OutgoingConnections
                .Where(connection => connection.DestinationTaskInstanceId.HasValue)
                .Select(connection => new BlueprintTaskConnectionViewModel
                {
                    ChannelId = actualResolvedVersion.OutputChannels.FirstOrDefault(channel => channel.Name == connection.ChannelName)?.Id,
                    ChannelName = connection.ChannelName,
                    TaskInstanceId = connection.DestinationTaskInstanceId!.Value,
                    Anchor = connection.Anchor,
                    Side = connection.Side,
                    Position = connection.Position,
                })
                .ToList(),
        };
    }

    private static ExecutionNodeDelta? BuildNodeDelta(
        TaskInstance taskInstance,
        IReadOnlyDictionary<Guid, TaskObject> executionTasksByBlueprintTaskId)
    {
        if (!executionTasksByBlueprintTaskId.TryGetValue(taskInstance.Id, out var taskState))
        {
            return null;
        }

        var actualResolvedVersion = ResolveActualVersion(taskInstance, taskState);

        return new ExecutionNodeDelta
        {
            InstanceId = taskInstance.Id,
            TaskInstanceId = Guid.Parse(taskState.Id),
            Status = MapTaskStatus(taskState.Status),
            ResolvedVersionId = taskState.ResolvedTaskVersionId ?? actualResolvedVersion.Id,
            ResolvedVersion = taskState.ResolvedTaskVersion ?? actualResolvedVersion.Version.ToString(),
            StartedAt = taskState.StartedAt,
            CompletedAt = taskState.CompletedAt,
            FailedAt = taskState.FailedAt,
            FailureMessage = taskState.ErrorMessage,
            UpdatedAt = taskState.UpdatedAt,
        };
    }

    private static TaskVersion ResolveActualVersion(TaskInstance taskInstance, TaskObject? taskState)
    {
        var fallbackResolvedVersion = ResolveConcreteVersion(taskInstance, requireActive: false);
        return taskState?.ResolvedTaskVersionId is int resolvedTaskVersionId
            ? taskInstance.TaskDefinition.Versions.FirstOrDefault(version => version.Id == resolvedTaskVersionId)
                ?? fallbackResolvedVersion
            : fallbackResolvedVersion;
    }

    private static string NormalizeWorkflowStatus(
        WorkflowExecutionStatus workflowStatus,
        IReadOnlyCollection<TaskObject> tasks)
    {
        if (tasks.Any(task => task.Status == TaskExecutionStatus.Failed))
        {
            return "Failed";
        }

        if (tasks.Any(task => task.Status == TaskExecutionStatus.InProgress))
        {
            return "InProgress";
        }

        if (tasks.Any(task => task.Status == TaskExecutionStatus.Pending))
        {
            return tasks.All(task => task.Status == TaskExecutionStatus.Pending)
                ? "Pending"
                : "InProgress";
        }

        if (tasks.Count > 0 && tasks.All(task => task.Status == TaskExecutionStatus.Completed))
        {
            return "Completed";
        }

        return workflowStatus.ToString();
    }

    private static string MapTaskStatus(TaskExecutionStatus status) =>
        status switch
        {
            TaskExecutionStatus.Completed => "Completed",
            TaskExecutionStatus.InProgress => "InProgress",
            TaskExecutionStatus.Failed => "Failed",
            _ => "Pending",
        };

    private static TaskVersion ResolveConcreteVersion(TaskInstance taskInstance, bool requireActive)
    {
        var laneVersions = taskInstance.TaskDefinition.Versions
            .Where(version => SemanticVersionUtility.IsSameLane(version.Version, taskInstance.AllowedMajor, taskInstance.AllowedMinor))
            .OrderByDescending(version => version.Version)
            .ToList();

        if (laneVersions.Count == 0)
        {
            throw new InvalidOperationException(
                $"No task version exists for task definition {taskInstance.TaskDefinitionId} lane {taskInstance.AllowedMajor}.{taskInstance.AllowedMinor}.");
        }

        _ = requireActive;
        return laneVersions.First();
    }

    private sealed record ExecutionProjectionContext(
        BlueprintDefinition Definition,
        BlueprintVersion BlueprintVersion,
        WorkflowObject<Dictionary<string, JsonElement>> Workflow,
        IReadOnlyCollection<TaskObject> ExecutionTasks,
        IReadOnlyDictionary<Guid, TaskObject> ExecutionTasksByBlueprintTaskId);
}
