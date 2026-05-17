using System.Diagnostics;
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
    private const int MaxProcessStateUpdateAttempts = 10;
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
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? PluginQueuedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public InputHydrationPlan? InputHydrationPlan { get; private set; }
    public ProcessStepIterationContext? IterationContext { get; private set; }
    public IReadOnlyDictionary<string, Dictionary<string, JsonElement>> ProcessNodeOverrides { get; private set; }
        = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
    public string[] OutputChannelsPopulated { get; set; } = [];
    public ExecutableBlueprintNode BlueprintNode => _blueprintNode.Value;
    public ProcessPhaseMetadata? PhaseMetadata { get; private set; }
    public Activity? ExecutionActivity { get; set; }

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

    public InvocationEnvelope ToInvocationEnvelope(InvocationTelemetryContext? telemetryContext = null) =>
        new(
            ExecutableBlueprintId,
            BlueprintDefinitionId,
            ProcessId,
            ProcessStepId,
            BlueprintNodeId,
            TaskDefinitionId,
            ResolvedTaskVersionId,
            BlueprintNode.ResolvedVersion.ToString(),
            PluginName,
            InputHydrationPlan ?? throw new InvalidOperationException(
                $"Process step {ProcessStepId} has no input hydration plan."))
        {
            TelemetryContext = telemetryContext
        };

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

        var (claimed, taskState) = await _processStepRepository.TryClaimProcessStepAsync(
            ProcessStepId,
            ProcessId,
            TaskDefinitionId,
            AllowedMajor,
            AllowedMinor,
            ResolvedTaskVersionId,
            BlueprintNode.ResolvedVersion.ToString(),
            now,
            cancellationToken);

        if (!claimed || taskState is null)
        {
            return (false, false);
        }

        InputHydrationPlan = taskState.InputHydrationPlan
            ?? throw new InvalidOperationException(
                $"Process step {ProcessStepId} has no input hydration plan.");

        StartedAt = taskState.StartedAt;
        ClaimedAt = taskState.StartedAt;
        IterationContext = taskState.IterationContext;

        var processState = await _processInstanceRepository.GetProcessStateById(
            ProcessId,
            ExecutableBlueprintId,
            cancellationToken);
        EnsureExecutionProgress(processState);
        ProcessNodeOverrides = processState.NodeOverrides;
        PhaseMetadata = processState.Phase;

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

    public async Task HydrateForFinalizationAsync(CancellationToken cancellationToken)
    {
        var stepState = await GetPersistedStepAsync(cancellationToken);
        ApplyStepState(stepState);

        var processState = await _processInstanceRepository.GetProcessStateById(
            ProcessId,
            ExecutableBlueprintId,
            cancellationToken);
        EnsureExecutionProgress(processState);
        ProcessNodeOverrides = processState.NodeOverrides;
        PhaseMetadata = processState.Phase;
    }

    public Task<ProcessStepRecord> GetPersistedStepAsync(CancellationToken cancellationToken) =>
        _processStepRepository.GetProcessStepById(
            ProcessStepId,
            ProcessId,
            cancellationToken);

    public async Task<bool> IsProcessCompletedAsync(CancellationToken cancellationToken)
    {
        var processState = await _processInstanceRepository.GetProcessStateById(
            ProcessId,
            ExecutableBlueprintId,
            cancellationToken);
        EnsureExecutionProgress(processState);
        return processState.Status is ProcessStatus.Completed;
    }

    public async Task<bool> PersistSuccessAsync(
        Dictionary<string, JsonElement>? effectiveInputData,
        Dictionary<string, JsonElement> outputData,
        Dictionary<string, JsonElement>? globalState,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        await _processStepRepository.CompleteProcessStepAsync(
            ProcessStepId,
            ProcessId,
            effectiveInputData is null ? null : NormalizeJsonMap(effectiveInputData),
            NormalizeJsonMap(outputData),
            completedAt,
            cancellationToken);

        return await UpdateProcessStateAfterSuccessAsync(globalState, completedAt, cancellationToken);
    }

    public async Task PersistFailureAsync(
        Dictionary<string, JsonElement>? effectiveInputData,
        Dictionary<string, JsonElement>? globalState,
        string? errorMessage,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        await _processStepRepository.FailProcessStepAsync(
            ProcessStepId,
            ProcessId,
            effectiveInputData is null ? null : NormalizeJsonMap(effectiveInputData),
            errorMessage,
            failedAt,
            cancellationToken);

        await UpdateProcessStateAfterFailureAsync(globalState, failedAt, cancellationToken);
    }

    public async Task CompleteMessageAsync(CancellationToken cancellationToken)
    {
        await _messageContainer.CompleteAsync(cancellationToken);
    }

    public async Task AbandonMessageAsync(CancellationToken cancellationToken)
    {
        await _messageContainer.AbandonAsync(cancellationToken);
    }

    private async Task<bool> UpdateProcessStateAfterSuccessAsync(
        Dictionary<string, JsonElement>? globalState,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        var normalizedGlobalState = globalState is null ? null : NormalizeJsonMap(globalState);
        for (var attempt = 0; attempt < MaxProcessStateUpdateAttempts; attempt++)
        {
            var processState = await _processInstanceRepository.GetProcessStateById(
                ProcessId,
                ExecutableBlueprintId,
                cancellationToken);

            EnsureExecutionProgress(processState);
            if (normalizedGlobalState is not null)
            {
                processState.GlobalState = normalizedGlobalState;
            }

            var wasCompleted = processState.Status is ProcessStatus.Completed;
            processState.ExecutionProgress.CompletedNodeIds.Add(BlueprintNodeId.ToString());
            processState.ExecutionProgress.CompletedStepIds.Add(ProcessStepId.ToString());
            processState.UpdatedAt = completedAt;
            if (processState.Status is not ProcessStatus.Failed)
            {
                processState.Status =
                    processState.ExecutionProgress.TotalStepCount > 0 &&
                    processState.ExecutionProgress.CompletedStepIds.Count >= processState.ExecutionProgress.TotalStepCount
                        ? ProcessStatus.Completed
                        : ProcessStatus.InProgress;
            }

            if (await _processInstanceRepository.TryUpdateProcessState(processState, cancellationToken))
            {
                return !wasCompleted && processState.Status is ProcessStatus.Completed;
            }
        }

        throw new InvalidOperationException(
            $"Could not update process progress for process {ProcessId} after {MaxProcessStateUpdateAttempts} attempts.");
    }

    private async Task UpdateProcessStateAfterFailureAsync(
        Dictionary<string, JsonElement>? globalState,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken)
    {
        var normalizedGlobalState = globalState is null ? null : NormalizeJsonMap(globalState);
        for (var attempt = 0; attempt < MaxProcessStateUpdateAttempts; attempt++)
        {
            var processState = await _processInstanceRepository.GetProcessStateById(
                ProcessId,
                ExecutableBlueprintId,
                cancellationToken);

            EnsureExecutionProgress(processState);
            if (normalizedGlobalState is not null)
            {
                processState.GlobalState = normalizedGlobalState;
            }

            processState.ExecutionProgress.FailedNodeIds.Add(BlueprintNodeId.ToString());
            processState.ExecutionProgress.FailedStepIds.Add(ProcessStepId.ToString());
            processState.Status = ProcessStatus.Failed;
            processState.UpdatedAt = failedAt;

            if (await _processInstanceRepository.TryUpdateProcessState(processState, cancellationToken))
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"Could not update failed process progress for process {ProcessId} after {MaxProcessStateUpdateAttempts} attempts.");
    }

    private void EnsureExecutionProgress(ProcessInstanceRecord<Dictionary<string, JsonElement>> processState)
    {
        processState.ExecutionProgress ??= new ProcessExecutionProgress();
        if (processState.ExecutionProgress.TotalNodeCount <= 0)
        {
            processState.ExecutionProgress.TotalNodeCount = _executableBlueprint.AllNodes.Count;
        }

        if (processState.ExecutionProgress.TotalStepCount <= 0)
        {
            processState.ExecutionProgress.TotalStepCount = processState.ExecutionProgress.TotalNodeCount;
        }
    }

    private void ApplyStepState(ProcessStepRecord stepState)
    {
        InputHydrationPlan = stepState.InputHydrationPlan;
        StartedAt = stepState.StartedAt;
        ClaimedAt = stepState.StartedAt;
        CompletedAt = stepState.CompletedAt ?? stepState.FailedAt;
        IterationContext = stepState.IterationContext;

        if (stepState.ResolvedTaskVersionId is int resolvedTaskVersionId)
        {
            BlueprintNode.ResolvedTaskVersionId = resolvedTaskVersionId;
        }

        if (!string.IsNullOrWhiteSpace(stepState.ResolvedTaskVersion) &&
            Version.TryParse(stepState.ResolvedTaskVersion, out var resolvedVersion))
        {
            BlueprintNode.ResolvedVersion = resolvedVersion;
        }
    }

    public async Task<bool> ApplyCollectionIteratorExpansionAsync(
        int repeatedBlueprintNodeCount,
        int itemCount,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var expansionKey = ProcessStepId.ToString();
        for (var attempt = 0; attempt < MaxProcessStateUpdateAttempts; attempt++)
        {
            var processState = await _processInstanceRepository.GetProcessStateById(
                ProcessId,
                ExecutableBlueprintId,
                cancellationToken);

            EnsureExecutionProgress(processState);
            if (!processState.ExecutionProgress.ExpandedCollectionIteratorStepIds.Add(expansionKey))
            {
                return processState.Status is ProcessStatus.Completed;
            }

            var repeatedStepDelta = repeatedBlueprintNodeCount * itemCount - repeatedBlueprintNodeCount;
            processState.ExecutionProgress.TotalStepCount = Math.Max(
                processState.ExecutionProgress.CompletedStepIds.Count,
                processState.ExecutionProgress.TotalStepCount + repeatedStepDelta);
            processState.ExecutionProgress.TotalNodeCount = processState.ExecutionProgress.TotalStepCount;
            processState.UpdatedAt = timestamp;
            if (processState.Status is not ProcessStatus.Failed)
            {
                processState.Status =
                    processState.ExecutionProgress.TotalStepCount > 0 &&
                    processState.ExecutionProgress.CompletedStepIds.Count >= processState.ExecutionProgress.TotalStepCount
                        ? ProcessStatus.Completed
                        : ProcessStatus.InProgress;
            }

            if (await _processInstanceRepository.TryUpdateProcessState(processState, cancellationToken))
            {
                return processState.Status is ProcessStatus.Completed;
            }
        }

        throw new InvalidOperationException(
            $"Could not update Collection Iterator expansion for process {ProcessId} after {MaxProcessStateUpdateAttempts} attempts.");
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
