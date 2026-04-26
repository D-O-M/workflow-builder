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

    [JsonPropertyName("outputData")]
    public Dictionary<string, JsonElement>? OutputData { get; set; }
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
