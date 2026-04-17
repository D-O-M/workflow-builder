using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

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
    private string _id = null!;

    [JsonPropertyName("id")]
    [JsonProperty(PropertyName = "id")]
    public required string id
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

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string WorkflowInstanceId
    {
        get => _id;
        init => _id = value;
    }

    [JsonPropertyName("blueprintId")]
    [JsonProperty(PropertyName = "blueprintId")]
    public required string BlueprintId { get; init; }

    [JsonPropertyName("createdAt")]
    [JsonProperty(PropertyName = "createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    [JsonProperty(PropertyName = "updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("status")]
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonProperty(PropertyName = "status")]
    public WorkflowExecutionStatus Status { get; set; }

    [JsonPropertyName("globalState")]
    [JsonProperty(PropertyName = "globalState")]
    public required TGlobalState GlobalState { get; set; }
}
