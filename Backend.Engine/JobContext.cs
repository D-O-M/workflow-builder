using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Database.State.Objects;
using Backend.Interfaces.Queues;
using Backend.Interfaces.Repositories;
using Backend.Models;
using Backend.Plugins.Communication;
using Task = System.Threading.Tasks.Task;

namespace Backend.Engine;

public class JobContext
{
    private static readonly JsonSerializerOptions NormalizedJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly CancellationTokenSource _cts = new();
    private readonly WorkflowTaskInitialisationMessage _message;
    private readonly ILogger _logger;
    private readonly IWorkflowTaskQueueMessage _messageContainer;
    private readonly ITaskInstanceRepository _taskInstanceRepository;
    private readonly WorkflowBlueprint _workflowBlueprint;
    private readonly IWorkflowInstanceRepository<Dictionary<string, JsonElement>> _workflowInstanceRepository;
    private readonly Lazy<WorkflowTask> _workflowTask;

    public Guid Guid { get; private set; }
    public WorkflowTaskInitialisationMessage Job => _message;
    public WorkflowBlueprint WorkflowBlueprint => _workflowBlueprint;
    public Guid WorkflowInstanceId => _message.WorkflowInstanceId;
    public Guid WorkflowTaskInstanceId => _message.WorkflowTaskInstanceId;
    public int WorkflowBlueprintId => _message.WorkflowBlueprintId;
    public Guid BlueprintTaskId => _message.BlueprintTaskId;
    public int ResolvedTaskVersionId => _workflowTask.Value.ResolvedTaskVersionId;
    public int TaskDefinitionId => _workflowTask.Value.TaskDefinitionId;
    public int AllowedMajor => _workflowTask.Value.AllowedMajor;
    public int AllowedMinor => _workflowTask.Value.AllowedMinor;
    public string InputChannelName => _workflowTask.Value.InputChannel.Name;
    public string LinkedOutputChannelName => _message.LinkedOutputChannelName;
    public Guid PreviousTaskInstanceId => _message.PreviousWorkflowTaskInstanceId;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string[] OutputChannelsPopulated { get; set; } = [];
    public WorkflowTask WorkflowTask => _workflowTask.Value;

    public DateTimeOffset ProcessingWindowExpiresAt => _messageContainer.LockedUntil;

    public JobContext(
        WorkflowTaskInitialisationMessage job,
        ILogger<JobContext> logger,
        IWorkflowTaskQueueMessage message,
        ITaskInstanceRepository taskInstanceRepository,
        IWorkflowInstanceRepository<Dictionary<string, JsonElement>> workflowInstanceRepository,
        WorkflowBlueprint workflowBlueprint)
    {
        _message = job;
        _logger = logger;
        _messageContainer = message;
        _taskInstanceRepository = taskInstanceRepository;
        _workflowBlueprint = workflowBlueprint;
        _workflowInstanceRepository = workflowInstanceRepository;
        _workflowTask = new Lazy<WorkflowTask>(
            () => _workflowBlueprint.AllTasks.First(task => task.Id == job.BlueprintTaskId));

        Guid = job.WorkflowTaskInstanceId;
    }

    public InvocationEnvelope ToInvocationEnvelope() =>
        new(
            WorkflowBlueprintId,
            WorkflowInstanceId,
            WorkflowTaskInstanceId,
            new PreviousTaskDetails(
                PreviousTaskInstanceId,
                LinkedOutputChannelName));

    public async Task<bool> ExtendProcessingWindow()
    {
        try
        {
            _logger.LogInformation(
                "Renewing lock on message {MessageId} for task {BlueprintTaskId} of Workflow Instance {WorkflowInstanceId} of Workflow Blueprint {WorkflowBlueprintId}",
                _messageContainer.MessageId,
                _message.BlueprintTaskId,
                _message.WorkflowInstanceId,
                _message.WorkflowBlueprintId);

            await _messageContainer.RenewLockAsync(_cts.Token);

            _logger.LogInformation(
                "Successfully renewed lock on message {MessageId} for task {BlueprintTaskId} of Workflow Instance {WorkflowInstanceId} of Workflow Blueprint {WorkflowBlueprintId}",
                _messageContainer.MessageId,
                _message.BlueprintTaskId,
                _message.WorkflowInstanceId,
                _message.WorkflowBlueprintId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to renew lock on message {MessageId} for task {BlueprintTaskId} of Workflow Instance {WorkflowInstanceId} of Workflow Blueprint {WorkflowBlueprintId}",
                _messageContainer.MessageId,
                _message.BlueprintTaskId,
                _message.WorkflowInstanceId,
                _message.WorkflowBlueprintId);

            return false;
        }
    }

    public async Task MarkInProgressAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var taskState = await _taskInstanceRepository.GetTaskInstanceById(
            WorkflowTaskInstanceId,
            WorkflowInstanceId,
            cancellationToken);

        taskState.Status = TaskExecutionStatus.InProgress;
        taskState.TaskDefinitionId = TaskDefinitionId;
        taskState.AllowedMajor = AllowedMajor;
        taskState.AllowedMinor = AllowedMinor;
        taskState.ResolvedTaskVersionId = ResolvedTaskVersionId;
        taskState.ResolvedTaskVersion = WorkflowTask.ResolvedVersion.ToString();
        taskState.UpdatedAt = now;
        await _taskInstanceRepository.UpdateTaskInstance(taskState, cancellationToken);

        var workflowState = await _workflowInstanceRepository.GetWorkflowStateById(
            WorkflowInstanceId,
            WorkflowBlueprintId,
            cancellationToken);

        if (workflowState.Status is WorkflowExecutionStatus.Pending)
        {
            workflowState.Status = WorkflowExecutionStatus.InProgress;
            workflowState.UpdatedAt = now;
            await _workflowInstanceRepository.UpdateWorkflowState(workflowState, cancellationToken);
        }
    }

    public async Task PersistSuccessAsync(
        Dictionary<string, JsonElement> outputData,
        Dictionary<string, JsonElement> globalState,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var taskState = await _taskInstanceRepository.GetTaskInstanceById(
            WorkflowTaskInstanceId,
            WorkflowInstanceId,
            cancellationToken);

        taskState.Status = TaskExecutionStatus.Completed;
        taskState.UpdatedAt = completedAt;
        taskState.CompletedAt = completedAt;
        taskState.FailedAt = null;
        taskState.OutputData = NormalizeJsonMap(outputData);
        await _taskInstanceRepository.UpdateTaskInstance(taskState, cancellationToken);

        var workflowState = await _workflowInstanceRepository.GetWorkflowStateById(
            WorkflowInstanceId,
            WorkflowBlueprintId,
            cancellationToken);

        workflowState.Status = WorkflowExecutionStatus.InProgress;
        workflowState.UpdatedAt = completedAt;
        workflowState.GlobalState = NormalizeJsonMap(globalState);
        await _workflowInstanceRepository.UpdateWorkflowState(workflowState, cancellationToken);
    }

    public async Task PersistFailureAsync(
        Dictionary<string, JsonElement> globalState,
        string? errorMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        var taskState = await _taskInstanceRepository.GetTaskInstanceById(
            WorkflowTaskInstanceId,
            WorkflowInstanceId,
            cancellationToken);

        taskState.Status = TaskExecutionStatus.Failed;
        taskState.UpdatedAt = failedAt;
        taskState.FailedAt = failedAt;
        taskState.ErrorMessage = errorMessage;
        await _taskInstanceRepository.UpdateTaskInstance(taskState, cancellationToken);

        var workflowState = await _workflowInstanceRepository.GetWorkflowStateById(
            WorkflowInstanceId,
            WorkflowBlueprintId,
            cancellationToken);

        workflowState.Status = WorkflowExecutionStatus.Failed;
        workflowState.UpdatedAt = failedAt;
        workflowState.GlobalState = NormalizeJsonMap(globalState);
        await _workflowInstanceRepository.UpdateWorkflowState(workflowState, cancellationToken);
    }

    public async Task CreatePendingTaskAsync(
        Guid taskInstanceId,
        Guid blueprintTaskId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var downstreamTask = _workflowBlueprint.AllTasks.First(task => task.Id == blueprintTaskId);

        await _taskInstanceRepository.CreateTaskInstance(
            new TaskObject
            {
                Id = taskInstanceId.ToString(),
                BlueprintTaskId = blueprintTaskId.ToString(),
                TaskDefinitionId = downstreamTask.TaskDefinitionId,
                AllowedMajor = downstreamTask.AllowedMajor,
                AllowedMinor = downstreamTask.AllowedMinor,
                WorkflowInstanceId = WorkflowInstanceId.ToString(),
                BlueprintId = WorkflowBlueprintId.ToString(),
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                Status = TaskExecutionStatus.Pending
            },
            cancellationToken);
    }

    public async Task CompleteMessageAsync(CancellationToken cancellationToken)
    {
        await _messageContainer.CompleteAsync(cancellationToken);
    }

    public async Task AbandonMessageAsync(CancellationToken cancellationToken)
    {
        await _messageContainer.AbandonAsync(cancellationToken);
    }

    private static Dictionary<string, JsonElement> NormalizeJsonMap(
        Dictionary<string, JsonElement> values) =>
        values.ToDictionary(
            entry => entry.Key,
            entry => NormalizeJsonElement(entry.Value),
            StringComparer.Ordinal);

    private static JsonElement NormalizeJsonElement(JsonElement element)
    {
        var node = BuildNormalizedNode(element);
        var normalizedJson = node?.ToJsonString(NormalizedJsonOptions) ?? "null";
        using var normalizedDocument = JsonDocument.Parse(normalizedJson);
        return normalizedDocument.RootElement.Clone();
    }

    private static JsonNode? BuildNormalizedNode(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => BuildNormalizedObject(element),
            JsonValueKind.Array => BuildNormalizedArray(element),
            JsonValueKind.String => JsonValue.Create(element.GetString()),
            JsonValueKind.Number => BuildNormalizedNumberValue(element),
            JsonValueKind.True or JsonValueKind.False => JsonValue.Create(element.GetBoolean()),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw new InvalidOperationException($"Unsupported JSON value kind '{element.ValueKind}'.")
        };

    private static JsonObject BuildNormalizedObject(JsonElement element)
    {
        var normalizedObject = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            normalizedObject[property.Name] = BuildNormalizedNode(property.Value);
        }

        return normalizedObject;
    }

    private static JsonArray BuildNormalizedArray(JsonElement element)
    {
        var normalizedArray = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            normalizedArray.Add(BuildNormalizedNode(item));
        }

        return normalizedArray;
    }

    private static JsonValue BuildNormalizedNumberValue(JsonElement element)
    {
        if (element.TryGetInt64(out var int64Value))
        {
            return JsonValue.Create(int64Value)!;
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            return JsonValue.Create(decimalValue)!;
        }

        return JsonValue.Create(element.GetDouble())!;
    }
}
