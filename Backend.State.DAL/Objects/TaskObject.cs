using System.Text.Json;
using System.Text.Json.Serialization;

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
    [JsonPropertyName("id")]
    public string Id { get; init; } = null!; // Row Key

    [JsonPropertyName("blueprintTaskId")]
    public string BlueprintTaskId { get; init; } = null!;

    [JsonPropertyName("workflowId")]
    public string WorkflowInstanceId { get; init; } = null!; // Partition Key

    [JsonPropertyName("blueprintId")]
    public string BlueprintId { get; init; } = null!;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("failedAt")]
    public DateTimeOffset? FailedAt { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TaskExecutionStatus Status { get; set; }

    [JsonPropertyName("outputData")]
    public Dictionary<string, JsonElement>? OutputData { get; set; }
}
