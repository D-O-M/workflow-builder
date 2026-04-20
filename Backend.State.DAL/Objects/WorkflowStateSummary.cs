using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Backend.Database.State.Objects;

public sealed class WorkflowStateSummary
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
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("status")]
    [System.Text.Json.Serialization.JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonProperty(PropertyName = "status")]
    public WorkflowExecutionStatus Status { get; init; }
}
