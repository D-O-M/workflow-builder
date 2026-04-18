using System.Text.Json;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models.InputModels;
using Backend.Plugins.Communication;

namespace Backend.API.Services;

public sealed record BlueprintExecutionAcceptedResult(
    Guid WorkflowInstanceId,
    int BlueprintId,
    DateTimeOffset AcceptedAt);

public sealed class BlueprintExecutionService
{
    private readonly ITaskInstanceRepository _taskInstanceRepository;
    private readonly IWorkflowTaskQueueWriter _workflowTaskQueueWriter;
    private readonly IWorkflowBlueprintRepository _workflowBlueprintRepository;
    private readonly IWorkflowInstanceRepository<Dictionary<string, JsonElement>> _workflowInstanceRepository;

    public BlueprintExecutionService(
        ITaskInstanceRepository taskInstanceRepository,
        IWorkflowTaskQueueWriter workflowTaskQueueWriter,
        IWorkflowBlueprintRepository workflowBlueprintRepository,
        IWorkflowInstanceRepository<Dictionary<string, JsonElement>> workflowInstanceRepository)
    {
        _taskInstanceRepository = taskInstanceRepository;
        _workflowTaskQueueWriter = workflowTaskQueueWriter;
        _workflowBlueprintRepository = workflowBlueprintRepository;
        _workflowInstanceRepository = workflowInstanceRepository;
    }

    public async Task<BlueprintExecutionAcceptedResult> ExecuteLatestVersionAsync(
        int blueprintDefinitionId,
        BlueprintExecutionInputModel executionModel,
        CancellationToken cancellationToken)
    {
        var blueprint = await _workflowBlueprintRepository.GetBlueprintById(
            blueprintDefinitionId,
            cancellationToken);

        var latestVersion = blueprint.Versions
            .OrderByDescending(version => version.Version)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Blueprint {blueprintDefinitionId} has no versions to execute.");

        var fullBlueprint = await _workflowBlueprintRepository.GetFullBlueprintById(
            latestVersion.BlueprintVersionId,
            cancellationToken);

        var acceptedAt = DateTimeOffset.UtcNow;
        var workflowInstanceId = Guid.NewGuid();
        var seedTaskInstanceId = Guid.NewGuid();
        var firstTaskInstanceId = Guid.NewGuid();
        var blueprintVersionIdString = latestVersion.BlueprintVersionId.ToString();

        var workflowState = new WorkflowObject<Dictionary<string, JsonElement>>
        {
            id = workflowInstanceId.ToString(),
            BlueprintId = blueprintVersionIdString,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            Status = WorkflowExecutionStatus.Pending,
            GlobalState = executionModel.GlobalState
        };

        var firstInputPayload = JsonSerializer.SerializeToElement(
            executionModel.InitialTaskInput,
            JsonSerializerOptions.Web);

        var triggerSeedState = new TaskObject
        {
            Id = seedTaskInstanceId.ToString(),
            BlueprintTaskId = Guid.Empty.ToString(),
            WorkflowInstanceId = workflowInstanceId.ToString(),
            BlueprintId = blueprintVersionIdString,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            CompletedAt = acceptedAt,
            Status = TaskExecutionStatus.Completed,
            OutputData = new Dictionary<string, JsonElement>
            {
                [fullBlueprint.InitialTask.InputChannel.Name] = firstInputPayload
            }
        };

        var initialTaskState = new TaskObject
        {
            Id = firstTaskInstanceId.ToString(),
            BlueprintTaskId = fullBlueprint.InitialTask.Id.ToString(),
            TaskDefinitionId = fullBlueprint.InitialTask.TaskDefinitionId,
            AllowedMajor = fullBlueprint.InitialTask.AllowedMajor,
            AllowedMinor = fullBlueprint.InitialTask.AllowedMinor,
            WorkflowInstanceId = workflowInstanceId.ToString(),
            BlueprintId = blueprintVersionIdString,
            CreatedAt = acceptedAt,
            UpdatedAt = acceptedAt,
            Status = TaskExecutionStatus.Pending
        };

        await _workflowInstanceRepository.CreateWorkflowState(workflowState, cancellationToken);
        await _taskInstanceRepository.CreateTaskInstance(triggerSeedState, cancellationToken);
        await _taskInstanceRepository.CreateTaskInstance(initialTaskState, cancellationToken);

        var initialisationMessage = new WorkflowTaskInitialisationMessage
        {
            WorkflowInstanceId = workflowInstanceId,
            WorkflowBlueprintId = latestVersion.BlueprintVersionId,
            WorkflowTaskInstanceId = firstTaskInstanceId,
            BlueprintTaskId = fullBlueprint.InitialTask.Id,
            PreviousWorkflowTaskInstanceId = seedTaskInstanceId,
            LinkedOutputChannelName = fullBlueprint.InitialTask.InputChannel.Name
        };

        await _workflowTaskQueueWriter.EnqueueAsync(initialisationMessage, cancellationToken);

        return new BlueprintExecutionAcceptedResult(
            workflowInstanceId,
            blueprintDefinitionId,
            acceptedAt);
    }
}
