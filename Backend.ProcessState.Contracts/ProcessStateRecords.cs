using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Database.State.Objects;

public enum ProcessStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

public enum PhasedProcessStatus
{
    Pending,
    InProgress,
    WaitingForTrigger,
    Completed,
    Failed
}

public enum ProcessStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

public enum ProcessStepDispatchStatus
{
    Pending,
    Dispatching,
    Dispatched,
    RetryableDispatchError,
    DispatchSkippedTerminal
}

public enum ProcessStepDispatchErrorKind
{
    RetryableServiceBusError,
    DispatchFailed
}

public enum ProcessStepFinalizationOutcome
{
    Succeeded,
    Failed
}

public enum ProcessStepFinalizationSource
{
    Plugin,
    CollectionIterator
}

public enum ProcessStepFinalizationStage
{
    ResultReceived = 0,
    OutputsPersisted = 1,
    FailurePersisted = 2,
    DownstreamActivationCompleted = 3,
    ProcessFinalizationCompleted = 4,
    QueueMessageCompletionPrepared = 5,
    [Obsolete("Use QueueMessageCompletionPrepared. The durable record is advanced before the broker message is completed.")]
    QueueMessageCompleted = QueueMessageCompletionPrepared
}

public enum ProcessStepFinalizationErrorKind
{
    RetryableFinalizationError,
    FinalizationFailed
}

public enum ProcessSummaryProjectionScope
{
    Tenant,
    BlueprintVersion
}

public class ProcessInstanceRecord<TGlobalState>
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string ProcessId
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("blueprintId")]
    public required string BlueprintId { get; init; }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStatus Status { get; set; }

    [JsonPropertyName("globalState")]
    public required TGlobalState GlobalState { get; set; }

    [JsonPropertyName("nodeOverrides")]
    public Dictionary<string, Dictionary<string, JsonElement>> NodeOverrides { get; set; } = new();

    [JsonPropertyName("executionProgress")]
    public ProcessExecutionProgress ExecutionProgress { get; set; } = new();

    [JsonPropertyName("phase")]
    public ProcessPhaseMetadata? Phase { get; set; }
}

public sealed class ProcessDirectoryRecord
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("processId")]
    public required string ProcessId { get; init; }

    [JsonPropertyName("blueprintDefinitionId")]
    public required int BlueprintDefinitionId { get; init; }

    [JsonPropertyName("blueprintVersionId")]
    public required int BlueprintVersionId { get; init; }

    [JsonPropertyName("processPartitionKey")]
    public required string ProcessPartitionKey { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ProcessStatus Status { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("failedAt")]
    public DateTimeOffset? FailedAt { get; init; }

    [JsonPropertyName("indexedAt")]
    public required DateTimeOffset IndexedAt { get; init; }
}

public sealed class ProcessSummaryProjectionRecord
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("partitionKey")]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("scope")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ProcessSummaryProjectionScope Scope { get; init; }

    [JsonPropertyName("scopeId")]
    public required string ScopeId { get; init; }

    [JsonPropertyName("shardKey")]
    public required int ShardKey { get; init; }

    [JsonPropertyName("timeBucket")]
    public required string TimeBucket { get; init; }

    [JsonPropertyName("processId")]
    public required string ProcessId { get; init; }

    [JsonPropertyName("blueprintDefinitionId")]
    public required int BlueprintDefinitionId { get; init; }

    [JsonPropertyName("blueprintVersionId")]
    public required int BlueprintVersionId { get; init; }

    [JsonPropertyName("processPartitionKey")]
    public required string ProcessPartitionKey { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ProcessStatus Status { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("failedAt")]
    public DateTimeOffset? FailedAt { get; init; }

    [JsonPropertyName("indexedAt")]
    public required DateTimeOffset IndexedAt { get; init; }
}

public class ProcessStepRecord
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("blueprintNodeId")]
    public string BlueprintNodeId { get; init; } = null!;

    [JsonPropertyName("taskDefinitionId")]
    public int? TaskDefinitionId { get; set; }

    [JsonPropertyName("allowedMajor")]
    public int? AllowedMajor { get; set; }

    [JsonPropertyName("allowedMinor")]
    public int? AllowedMinor { get; set; }

    [JsonPropertyName("resolvedTaskVersionId")]
    public int? ResolvedTaskVersionId { get; set; }

    [JsonPropertyName("resolvedTaskVersion")]
    public string? ResolvedTaskVersion { get; set; }

    [JsonPropertyName("processId")]
    public string ProcessId { get; init; } = null!;

    [JsonPropertyName("blueprintId")]
    public string BlueprintId { get; init; } = null!;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("failedAt")]
    public DateTimeOffset? FailedAt { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStepStatus Status { get; set; }

    [JsonPropertyName("inputData")]
    public Dictionary<string, JsonElement>? InputData { get; set; }

    [JsonPropertyName("inputHydrationPlan")]
    public InputHydrationPlan? InputHydrationPlan { get; set; }

    [JsonPropertyName("outputData")]
    public Dictionary<string, JsonElement>? OutputData { get; set; }

    [JsonPropertyName("iterationContext")]
    public ProcessStepIterationContext? IterationContext { get; set; }

    [JsonPropertyName("dispatch")]
    public ProcessStepDispatchState? Dispatch { get; set; }
}

public sealed class ProcessStepDispatchState
{
    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStepDispatchStatus Status { get; set; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("attemptCount")]
    public int AttemptCount { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("nextAttemptAt")]
    public DateTimeOffset? NextAttemptAt { get; set; }

    [JsonPropertyName("dispatchingStartedAt")]
    public DateTimeOffset? DispatchingStartedAt { get; set; }

    [JsonPropertyName("lockedUntil")]
    public DateTimeOffset? LockedUntil { get; set; }

    [JsonPropertyName("leaseId")]
    public string? LeaseId { get; set; }

    [JsonPropertyName("dispatchedAt")]
    public DateTimeOffset? DispatchedAt { get; set; }

    [JsonPropertyName("lastErrorKind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStepDispatchErrorKind? LastErrorKind { get; set; }

    [JsonPropertyName("lastErrorMessage")]
    public string? LastErrorMessage { get; set; }

    [JsonPropertyName("lastErrorAt")]
    public DateTimeOffset? LastErrorAt { get; set; }
}

public sealed class ProcessStepFinalizationRecord
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("processId")]
    public required string ProcessId { get; init; }

    [JsonPropertyName("blueprintId")]
    public required string BlueprintId { get; init; }

    [JsonPropertyName("blueprintNodeId")]
    public required string BlueprintNodeId { get; init; }

    [JsonPropertyName("taskDefinitionId")]
    public int? TaskDefinitionId { get; set; }

    [JsonPropertyName("resolvedTaskVersionId")]
    public int? ResolvedTaskVersionId { get; set; }

    [JsonPropertyName("resolvedTaskVersion")]
    public string? ResolvedTaskVersion { get; set; }

    [JsonPropertyName("outcome")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStepFinalizationOutcome Outcome { get; set; }

    [JsonPropertyName("source")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStepFinalizationSource Source { get; set; }

    [JsonPropertyName("stage")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStepFinalizationStage Stage { get; set; }

    [JsonPropertyName("effectiveInputData")]
    public Dictionary<string, JsonElement>? EffectiveInputData { get; set; }

    [JsonPropertyName("outputData")]
    public Dictionary<string, JsonElement>? OutputData { get; set; }

    [JsonPropertyName("globalState")]
    public Dictionary<string, JsonElement>? GlobalState { get; set; }

    [JsonPropertyName("timingsMs")]
    public Dictionary<string, double>? TimingsMs { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; set; }

    [JsonPropertyName("collectionIteratorItemCount")]
    public int? CollectionIteratorItemCount { get; set; }

    [JsonPropertyName("collectionIteratorRepeatedBlueprintNodeCount")]
    public int? CollectionIteratorRepeatedBlueprintNodeCount { get; set; }

    [JsonPropertyName("collectionIteratorBatchSize")]
    public int? CollectionIteratorBatchSize { get; set; }

    [JsonPropertyName("lastErrorKind")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStepFinalizationErrorKind? LastErrorKind { get; set; }

    [JsonPropertyName("lastErrorMessage")]
    public string? LastErrorMessage { get; set; }

    [JsonPropertyName("lastErrorAt")]
    public DateTimeOffset? LastErrorAt { get; set; }

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class ProcessExecutionProgress
{
    [JsonPropertyName("totalNodeCount")]
    public int TotalNodeCount { get; set; }

    [JsonPropertyName("totalStepCount")]
    public int TotalStepCount { get; set; }

    [JsonPropertyName("completedNodeIds")]
    public HashSet<string> CompletedNodeIds { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("failedNodeIds")]
    public HashSet<string> FailedNodeIds { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("completedStepIds")]
    public HashSet<string> CompletedStepIds { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("failedStepIds")]
    public HashSet<string> FailedStepIds { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("expandedCollectionIteratorStepIds")]
    public HashSet<string> ExpandedCollectionIteratorStepIds { get; set; } = new(StringComparer.Ordinal);
}

public sealed class InputHydrationPlan
{
    [JsonPropertyName("inputChannelName")]
    public required string InputChannelName { get; init; }

    [JsonPropertyName("initialInput")]
    public Dictionary<string, JsonElement> InitialInput { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("sources")]
    public List<InputHydrationSource> Sources { get; init; } = [];

    [JsonPropertyName("designTimeInputOverrides")]
    public Dictionary<string, JsonElement> DesignTimeInputOverrides { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("runtimeInputOverrides")]
    public Dictionary<string, JsonElement> RuntimeInputOverrides { get; init; } = new(StringComparer.Ordinal);
}

public sealed class InputHydrationSource
{
    [JsonPropertyName("bindingId")]
    public required string BindingId { get; init; }

    [JsonPropertyName("sourceProcessStepId")]
    public required string SourceProcessStepId { get; init; }

    [JsonPropertyName("sourceOutputChannelName")]
    public required string SourceOutputChannelName { get; init; }

    [JsonPropertyName("propertyMappings")]
    public List<InputHydrationPropertyMapping> PropertyMappings { get; init; } = [];

    [JsonPropertyName("inlineInput")]
    public Dictionary<string, JsonElement>? InlineInput { get; init; }
}

public sealed class InputHydrationPropertyMapping
{
    [JsonPropertyName("sourceProperty")]
    public required string SourceProperty { get; init; }

    [JsonPropertyName("targetProperty")]
    public required string TargetProperty { get; init; }
}

public sealed class ProcessStepIterationContext
{
    [JsonPropertyName("sourceIteratorStepId")]
    public required string SourceIteratorStepId { get; init; }

    [JsonPropertyName("sourceIteratorBlueprintNodeId")]
    public required string SourceIteratorBlueprintNodeId { get; init; }

    [JsonPropertyName("itemIndex")]
    public required int ItemIndex { get; init; }

    [JsonPropertyName("itemKey")]
    public required string ItemKey { get; init; }
}

public sealed class ProcessPhaseMetadata
{
    [JsonPropertyName("phasedProcessId")]
    public required string PhasedProcessId { get; init; }

    [JsonPropertyName("phasedBlueprintDefinitionId")]
    public required int PhasedBlueprintDefinitionId { get; init; }

    [JsonPropertyName("phasedBlueprintVersionId")]
    public required int PhasedBlueprintVersionId { get; init; }

    [JsonPropertyName("phaseId")]
    public required string PhaseId { get; init; }

    [JsonPropertyName("phaseIndex")]
    public required int PhaseIndex { get; init; }

    [JsonPropertyName("phaseName")]
    public required string PhaseName { get; init; }
}

public sealed class PhasedProcessRecord
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("phasedBlueprintDefinitionId")]
    public required string PhasedBlueprintDefinitionId { get; init; }

    [JsonPropertyName("phasedBlueprintVersionId")]
    public required int PhasedBlueprintVersionId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PhasedProcessStatus Status { get; set; }

    [JsonPropertyName("currentPhaseId")]
    public string? CurrentPhaseId { get; set; }

    [JsonPropertyName("phases")]
    public List<PhasedProcessPhaseRecord> Phases { get; init; } = [];

    [JsonPropertyName("phaseResults")]
    public Dictionary<string, Dictionary<string, JsonElement>> PhaseResults { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PhasedProcessPhaseRecord
{
    [JsonPropertyName("phaseId")]
    public required string PhaseId { get; init; }

    [JsonPropertyName("phaseIndex")]
    public required int PhaseIndex { get; init; }

    [JsonPropertyName("phaseName")]
    public required string PhaseName { get; init; }

    [JsonPropertyName("blueprintDefinitionId")]
    public required int BlueprintDefinitionId { get; init; }

    [JsonPropertyName("blueprintVersionId")]
    public int? BlueprintVersionId { get; set; }

    [JsonPropertyName("childProcessId")]
    public string? ChildProcessId { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PhasedProcessStatus Status { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("triggerInput")]
    public Dictionary<string, JsonElement> TriggerInput { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("failureMessage")]
    public string? FailureMessage { get; set; }
}

public sealed class PhaseCorrelationRecord
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("targetPhaseKey")]
    public required string TargetPhaseKey { get; init; }

    [JsonPropertyName("phasedProcessId")]
    public required string PhasedProcessId { get; init; }

    [JsonPropertyName("phasedBlueprintDefinitionId")]
    public required int PhasedBlueprintDefinitionId { get; init; }

    [JsonPropertyName("phasedBlueprintVersionId")]
    public required int PhasedBlueprintVersionId { get; init; }

    [JsonPropertyName("transitionId")]
    public required string TransitionId { get; init; }

    [JsonPropertyName("sourcePhaseId")]
    public required string SourcePhaseId { get; init; }

    [JsonPropertyName("targetPhaseId")]
    public required string TargetPhaseId { get; init; }

    [JsonPropertyName("targetPhaseIndex")]
    public required int TargetPhaseIndex { get; init; }

    [JsonPropertyName("values")]
    public Dictionary<string, JsonElement> Values { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }
}

public class NodeActivationRecord
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("processId")]
    public required string ProcessId { get; init; }

    [JsonPropertyName("blueprintId")]
    public required string BlueprintId { get; init; }

    [JsonPropertyName("blueprintNodeId")]
    public required string BlueprintNodeId { get; init; }

    [JsonPropertyName("bindingId")]
    public required string BindingId { get; init; }

    [JsonPropertyName("sourceProcessStepId")]
    public required string SourceProcessStepId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("materializedInput")]
    public Dictionary<string, JsonElement> MaterializedInput { get; init; } = new();
}

public sealed class NodeJoinStateRecord
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    [JsonPropertyName("processId")]
    public required string ProcessId { get; init; }

    [JsonPropertyName("blueprintId")]
    public required string BlueprintId { get; init; }

    [JsonPropertyName("blueprintNodeId")]
    public required string BlueprintNodeId { get; init; }

    [JsonPropertyName("requiredBindingIds")]
    public HashSet<string> RequiredBindingIds { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("receivedSources")]
    public Dictionary<string, InputHydrationSource> ReceivedSources { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("stepEnqueued")]
    public bool StepEnqueued { get; set; }

    [JsonPropertyName("enqueuedProcessStepId")]
    public string? EnqueuedProcessStepId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class ProcessStateSummary
{
    private string _id = null!;

    [JsonPropertyName("id")]
    public required string id
    {
        get => _id;
        init => _id = value;
    }

    [JsonIgnore]
    public string ProcessId
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("blueprintId")]
    public required string BlueprintId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProcessStatus Status { get; init; }
}

public sealed class ProcessStateSummaryPage
{
    public required IReadOnlyList<ProcessStateSummary> Items { get; init; }
    public string? ContinuationToken { get; init; }
}

public sealed class ProcessSummaryProjectionPage
{
    public required IReadOnlyList<ProcessSummaryProjectionRecord> Items { get; init; }
    public string? ContinuationToken { get; init; }
}
