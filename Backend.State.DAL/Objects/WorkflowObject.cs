using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace Backend.Database.State.Objects;

public enum WorkflowExecutionStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

public class WorkflowObject<TGlobalState>
{
    [JsonPropertyName("id")]
    public required string WorkflowInstanceId { get; init; }

    [JsonPropertyName("blueprintId")]
    public required string BlueprintId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WorkflowExecutionStatus Status { get; set; }

    [JsonPropertyName("globalState")]
    public required TGlobalState GlobalState { get; set; }
}
