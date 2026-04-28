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

public enum ProcessStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
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
}

public sealed class ProcessExecutionProgress
{
    [JsonPropertyName("totalNodeCount")]
    public int TotalNodeCount { get; set; }

    [JsonPropertyName("completedNodeIds")]
    public HashSet<string> CompletedNodeIds { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("failedNodeIds")]
    public HashSet<string> FailedNodeIds { get; set; } = new(StringComparer.Ordinal);
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
}

public sealed class InputHydrationPropertyMapping
{
    [JsonPropertyName("sourceProperty")]
    public required string SourceProperty { get; init; }

    [JsonPropertyName("targetProperty")]
    public required string TargetProperty { get; init; }
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
