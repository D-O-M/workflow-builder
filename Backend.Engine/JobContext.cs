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
    private readonly ProcessStepInitialisationMessage _message;
    private readonly ILogger _logger;
    private readonly IProcessStepQueueMessage _messageContainer;
    private readonly IProcessStepRepository _processStepRepository;
    private readonly ExecutableBlueprint _executableBlueprint;
    private readonly IProcessInstanceRepository<Dictionary<string, JsonElement>> _processInstanceRepository;
    private readonly Lazy<ExecutableBlueprintNode> _blueprintNode;

    public Guid Guid { get; private set; }
    public ProcessStepInitialisationMessage Job => _message;
    public ExecutableBlueprint ExecutableBlueprint => _executableBlueprint;
    public Guid ProcessId => _message.ProcessId;
    public Guid ProcessStepId => _message.ProcessStepId;
    public int ExecutableBlueprintId => _message.ExecutableBlueprintId;
    public int BlueprintDefinitionId => _executableBlueprint.DefinitionId;
    public int BlueprintVersionNumber => _executableBlueprint.VersionNumber;
    public string BlueprintName => _executableBlueprint.Name;
    public Guid BlueprintNodeId => _message.BlueprintNodeId;
    public int ResolvedTaskVersionId => _blueprintNode.Value.ResolvedTaskVersionId;
    public int TaskDefinitionId => _blueprintNode.Value.TaskDefinitionId;
    public int AllowedMajor => _blueprintNode.Value.AllowedMajor;
    public int AllowedMinor => _blueprintNode.Value.AllowedMinor;
    public string TaskName => _blueprintNode.Value.Name;
    public string PluginName => _blueprintNode.Value.Name;
    public string InputChannelName => _blueprintNode.Value.InputChannel.Name;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string[] OutputChannelsPopulated { get; set; } = [];
    public ExecutableBlueprintNode BlueprintNode => _blueprintNode.Value;

    public DateTimeOffset ProcessingWindowExpiresAt => _messageContainer.LockedUntil;

    public JobContext(
        ProcessStepInitialisationMessage job,
        ILogger<JobContext> logger,
        IProcessStepQueueMessage message,
        IProcessStepRepository processStepRepository,
        IProcessInstanceRepository<Dictionary<string, JsonElement>> processInstanceRepository,
        ExecutableBlueprint executableBlueprint)
    {
        _message = job;
        _logger = logger;
        _messageContainer = message;
        _processStepRepository = processStepRepository;
        _executableBlueprint = executableBlueprint;
        _processInstanceRepository = processInstanceRepository;
        _blueprintNode = new Lazy<ExecutableBlueprintNode>(
            () => _executableBlueprint.NodesById[job.BlueprintNodeId]);

        Guid = job.ProcessStepId;
    }

    public InvocationEnvelope ToInvocationEnvelope() =>
        new(
            ExecutableBlueprintId,
            BlueprintDefinitionId,
            ProcessId,
            ProcessStepId,
            BlueprintNodeId,
            TaskDefinitionId,
            ResolvedTaskVersionId,
            BlueprintNode.ResolvedVersion.ToString(),
            PluginName);

    public async Task<bool> ExtendProcessingWindow()
    {
        try
        {
            _logger.LogInformation(
                "Renewing lock on message {MessageId} for step {BlueprintNodeId} of process {ProcessId} of blueprint version {ExecutableBlueprintId}",
                _messageContainer.MessageId,
                _message.BlueprintNodeId,
                _message.ProcessId,
                _message.ExecutableBlueprintId);

            await _messageContainer.RenewLockAsync(_cts.Token);

            _logger.LogInformation(
                "Successfully renewed lock on message {MessageId} for step {BlueprintNodeId} of process {ProcessId} of blueprint version {ExecutableBlueprintId}",
                _messageContainer.MessageId,
                _message.BlueprintNodeId,
                _message.ProcessId,
                _message.ExecutableBlueprintId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to renew lock on message {MessageId} for step {BlueprintNodeId} of process {ProcessId} of blueprint version {ExecutableBlueprintId}",
                _messageContainer.MessageId,
                _message.BlueprintNodeId,
                _message.ProcessId,
                _message.ExecutableBlueprintId);

            return false;
        }
    }

    public async Task<(bool StepClaimed, bool ProcessStarted)> TryMarkInProgressAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var taskState = await _processStepRepository.GetProcessStepById(
            ProcessStepId,
            ProcessId,
            cancellationToken);

        if (taskState.Status is not ProcessStepStatus.Pending)
        {
            return (false, false);
        }

        taskState.Status = ProcessStepStatus.InProgress;
        taskState.TaskDefinitionId = TaskDefinitionId;
        taskState.AllowedMajor = AllowedMajor;
        taskState.AllowedMinor = AllowedMinor;
        taskState.ResolvedTaskVersionId = ResolvedTaskVersionId;
        taskState.ResolvedTaskVersion = BlueprintNode.ResolvedVersion.ToString();
        taskState.UpdatedAt = now;
        taskState.StartedAt ??= now;

        if (!await _processStepRepository.TryUpdateProcessStep(taskState, cancellationToken))
        {
            return (false, false);
        }

        StartedAt = taskState.StartedAt;

        var processState = await _processInstanceRepository.GetProcessStateById(
            ProcessId,
            ExecutableBlueprintId,
            cancellationToken);

        if (processState.Status is ProcessStatus.Pending)
        {
            processState.Status = ProcessStatus.InProgress;
            processState.UpdatedAt = now;
            await _processInstanceRepository.UpdateProcessState(processState, cancellationToken);
            return (true, true);
        }

        return (true, false);
    }

    public async Task ResetToPendingAsync(CancellationToken cancellationToken)
    {
        var taskState = await _processStepRepository.GetProcessStepById(
            ProcessStepId,
            ProcessId,
            cancellationToken);

        if (taskState.Status is not ProcessStepStatus.InProgress)
        {
            return;
        }

        taskState.Status = ProcessStepStatus.Pending;
        taskState.UpdatedAt = DateTimeOffset.UtcNow;
        taskState.StartedAt = null;
        await _processStepRepository.UpdateProcessStep(taskState, cancellationToken);
    }

    public async Task PersistSuccessAsync(
        Dictionary<string, JsonElement> outputData,
        Dictionary<string, JsonElement> globalState,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var taskState = await _processStepRepository.GetProcessStepById(
            ProcessStepId,
            ProcessId,
            cancellationToken);

        taskState.Status = ProcessStepStatus.Completed;
        taskState.UpdatedAt = completedAt;
        taskState.CompletedAt = completedAt;
        taskState.FailedAt = null;
        taskState.OutputData = NormalizeJsonMap(outputData);
        await _processStepRepository.UpdateProcessStep(taskState, cancellationToken);

        var processState = await _processInstanceRepository.GetProcessStateById(
            ProcessId,
            ExecutableBlueprintId,
            cancellationToken);

        processState.Status = ProcessStatus.InProgress;
        processState.UpdatedAt = completedAt;
        processState.GlobalState = NormalizeJsonMap(globalState);
        await _processInstanceRepository.UpdateProcessState(processState, cancellationToken);
    }

    public async Task PersistFailureAsync(
        Dictionary<string, JsonElement> globalState,
        string? errorMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        var taskState = await _processStepRepository.GetProcessStepById(
            ProcessStepId,
            ProcessId,
            cancellationToken);

        taskState.Status = ProcessStepStatus.Failed;
        taskState.UpdatedAt = failedAt;
        taskState.FailedAt = failedAt;
        taskState.ErrorMessage = errorMessage;
        await _processStepRepository.UpdateProcessStep(taskState, cancellationToken);

        var processState = await _processInstanceRepository.GetProcessStateById(
            ProcessId,
            ExecutableBlueprintId,
            cancellationToken);

        processState.Status = ProcessStatus.Failed;
        processState.UpdatedAt = failedAt;
        processState.GlobalState = NormalizeJsonMap(globalState);
        await _processInstanceRepository.UpdateProcessState(processState, cancellationToken);
    }

    public async Task CompleteMessageAsync(CancellationToken cancellationToken)
    {
        await _messageContainer.CompleteAsync(cancellationToken);
    }

    public async Task AbandonMessageAsync(CancellationToken cancellationToken)
    {
        await _messageContainer.AbandonAsync(cancellationToken);
    }

    public async Task<bool> CompleteProcessIfTerminalAsync(CancellationToken cancellationToken)
    {
        var steps = await _processStepRepository.GetProcessStepsForProcess(
            ProcessId,
            cancellationToken);

        var realSteps = steps
            .Where(step => Guid.TryParse(step.BlueprintNodeId, out var blueprintNodeId) && blueprintNodeId != Guid.Empty)
            .ToList();

        if (realSteps.Count != _executableBlueprint.AllNodes.Count ||
            realSteps.Any(step => step.Status != ProcessStepStatus.Completed))
        {
            return false;
        }

        var processState = await _processInstanceRepository.GetProcessStateById(
            ProcessId,
            ExecutableBlueprintId,
            cancellationToken);

        if (processState.Status == ProcessStatus.Completed)
        {
            return false;
        }

        processState.Status = ProcessStatus.Completed;
        processState.UpdatedAt = CompletedAt ?? DateTimeOffset.UtcNow;
        await _processInstanceRepository.UpdateProcessState(processState, cancellationToken);
        return true;
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
