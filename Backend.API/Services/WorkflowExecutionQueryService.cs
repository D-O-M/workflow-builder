using System.Text.Json;
using Backend.Database.Relational;
using Backend.Database.Relational.Entities;
using Backend.Database.State.Objects;
using Backend.Interfaces.Repositories;
using Backend.Models.ViewModels;
using Microsoft.EntityFrameworkCore;
using Backend.Repositories;

namespace Backend.API.Services;

public sealed class WorkflowExecutionQueryService
{
    private readonly WorkflowDbContext _dbContext;
    private readonly ITaskInstanceRepository _taskInstanceRepository;
    private readonly IWorkflowInstanceRepository<Dictionary<string, JsonElement>> _workflowInstanceRepository;

    public WorkflowExecutionQueryService(
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

                summaries.Add(new WorkflowExecutionSummaryViewModel
                {
                    WorkflowInstanceId = Guid.Parse(workflow.WorkflowInstanceId),
                    BlueprintVersionId = version.Id,
                    BlueprintVersionNumber = version.Version,
                    BlueprintVersionName = version.Name,
                    Status = NormalizeWorkflowStatus(workflow.Status, tasks),
                    CreatedAt = workflow.CreatedAt,
                    UpdatedAt = workflow.UpdatedAt,
                    CompletedTaskCount = tasks.Count(task => task.Status == TaskExecutionStatus.Completed),
                    FailedTaskCount = tasks.Count(task => task.Status == TaskExecutionStatus.Failed),
                    InProgressTaskCount = tasks.Count(task => task.Status == TaskExecutionStatus.InProgress),
                    PendingTaskCount = tasks.Count(task => task.Status == TaskExecutionStatus.Pending),
                });
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
        var definition = await _dbContext.BlueprintDefinitions
            .Include(d => d.Versions)
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == blueprintDefinitionId, cancellationToken);

        if (definition is null)
        {
            throw new EntityNotFoundException<BlueprintViewModel>(blueprintDefinitionId);
        }

        WorkflowObject<Dictionary<string, JsonElement>>? workflow = null;
        BlueprintVersion? blueprintVersion = null;

        foreach (var version in definition.Versions.OrderByDescending(v => v.Version))
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

        var blueprintVersionEntity = await _dbContext.BlueprintVersions
            .Include(version => version.TaskInstances)
                .ThenInclude(taskInstance => taskInstance.TaskVersion)
                    .ThenInclude(taskVersion => taskVersion.TaskDefinition)
            .Include(version => version.TaskInstances)
                .ThenInclude(taskInstance => taskInstance.TaskVersion)
                    .ThenInclude(taskVersion => taskVersion.Channels)
            .Include(version => version.TaskInstances)
                .ThenInclude(taskInstance => taskInstance.OutgoingConnections)
                    .ThenInclude(connection => connection.Channel)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(version => version.Id == blueprintVersion.Id, cancellationToken);

        if (blueprintVersionEntity is null)
        {
            throw new EntityNotFoundException<BlueprintVersionViewModel>(blueprintVersion.Id);
        }

        var executionTasks = await _taskInstanceRepository.GetTaskInstancesForWorkflow(
            workflowInstanceId,
            cancellationToken);
        var executionTasksByBlueprintTaskId = executionTasks
            .Where(task => Guid.TryParse(task.BlueprintTaskId, out _))
            .ToDictionary(task => Guid.Parse(task.BlueprintTaskId), task => task);

        var nodes = blueprintVersionEntity.TaskInstances
            .OrderBy(taskInstance => taskInstance.PositionY)
            .ThenBy(taskInstance => taskInstance.PositionX)
            .Select(taskInstance =>
            {
                var inputChannelName = taskInstance.TaskVersion.InputChannels.SingleOrDefault()?.Name;
                var outputChannelNames = taskInstance.TaskVersion.OutputChannels
                    .OrderBy(channel => channel.Id)
                    .Select(channel => channel.Name)
                    .ToList();

                var status = executionTasksByBlueprintTaskId.TryGetValue(taskInstance.Id, out var taskState)
                    ? MapTaskStatus(taskState.Status)
                    : "NotExecuted";

                return new WorkflowExecutionNodeViewModel
                {
                    InstanceId = taskInstance.Id,
                    VersionId = taskInstance.TaskVersionId,
                    Name = taskInstance.TaskVersion.TaskDefinition!.Name,
                    Status = status,
                    FailureMessage = taskState?.ErrorMessage,
                    Position = new CoordinatesViewModel
                    {
                        X = taskInstance.PositionX,
                        Y = taskInstance.PositionY,
                    },
                    InputChannelName = inputChannelName,
                    OutputChannelNames = outputChannelNames,
                    OutgoingConnections = taskInstance.OutgoingConnections
                        .Where(connection => connection.DestinationTaskInstanceId.HasValue)
                        .Select(connection => new BlueprintTaskConnectionViewModel
                        {
                            ChannelId = connection.ChannelId,
                            ChannelName = connection.Channel?.Name,
                            TaskInstanceId = connection.DestinationTaskInstanceId!.Value,
                            Anchor = connection.Anchor,
                            Side = connection.Side,
                            Position = connection.Position,
                        })
                        .ToList(),
                };
            })
            .ToList();

        return new WorkflowExecutionDetailViewModel
        {
            WorkflowInstanceId = workflowInstanceId,
            BlueprintDefinitionId = definition.Id,
            BlueprintName = definition.Key,
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
}
