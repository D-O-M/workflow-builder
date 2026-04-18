using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Backend.Database.State.Objects;

public enum TaskExecutionStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

public class TaskObject
{
    private string _id = null!;

    [JsonPropertyName("id")]
    [JsonProperty(PropertyName = "id")]
    public string id
    {
        get => _id;
        init => _id = value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string Id
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("blueprintTaskId")]
    [JsonProperty(PropertyName = "blueprintTaskId")]
    public string BlueprintTaskId { get; init; } = null!;

    [JsonPropertyName("taskDefinitionId")]
    [JsonProperty(PropertyName = "taskDefinitionId")]
    public int? TaskDefinitionId { get; set; }

    [JsonPropertyName("allowedMajor")]
    [JsonProperty(PropertyName = "allowedMajor")]
    public int? AllowedMajor { get; set; }

    [JsonPropertyName("allowedMinor")]
    [JsonProperty(PropertyName = "allowedMinor")]
    public int? AllowedMinor { get; set; }

    [JsonPropertyName("resolvedTaskVersionId")]
    [JsonProperty(PropertyName = "resolvedTaskVersionId")]
    public int? ResolvedTaskVersionId { get; set; }

    [JsonPropertyName("resolvedTaskVersion")]
    [JsonProperty(PropertyName = "resolvedTaskVersion")]
    public string? ResolvedTaskVersion { get; set; }

    [JsonPropertyName("workflowId")]
    [JsonProperty(PropertyName = "workflowId")]
    public string WorkflowInstanceId { get; init; } = null!; // Partition Key

    [JsonPropertyName("blueprintId")]
    [JsonProperty(PropertyName = "blueprintId")]
    public string BlueprintId { get; init; } = null!;

    [JsonPropertyName("createdAt")]
    [JsonProperty(PropertyName = "createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("updatedAt")]
    [JsonProperty(PropertyName = "updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("completedAt")]
    [JsonProperty(PropertyName = "completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("failedAt")]
    [JsonProperty(PropertyName = "failedAt")]
    public DateTimeOffset? FailedAt { get; set; }

    [JsonPropertyName("errorMessage")]
    [JsonProperty(PropertyName = "errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("status")]
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonProperty(PropertyName = "status")]
    public TaskExecutionStatus Status { get; set; }

    [JsonPropertyName("outputData")]
    [JsonProperty(PropertyName = "outputData")]
    public Dictionary<string, JsonElement>? OutputData { get; set; }
}
