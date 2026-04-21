using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Backend.Database.State.Objects;

public class NodeActivationRecord
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

    [JsonPropertyName("processId")]
    [JsonProperty(PropertyName = "processId")]
    public required string ProcessId { get; init; }

    [JsonPropertyName("blueprintId")]
    [JsonProperty(PropertyName = "blueprintId")]
    public required string BlueprintId { get; init; }

    [JsonPropertyName("blueprintNodeId")]
    [JsonProperty(PropertyName = "blueprintNodeId")]
    public required string BlueprintNodeId { get; init; }

    [JsonPropertyName("bindingId")]
    [JsonProperty(PropertyName = "bindingId")]
    public required string BindingId { get; init; }

    [JsonPropertyName("sourceProcessStepId")]
    [JsonProperty(PropertyName = "sourceProcessStepId")]
    public required string SourceProcessStepId { get; init; }

    [JsonPropertyName("createdAt")]
    [JsonProperty(PropertyName = "createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    [JsonProperty(PropertyName = "updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("materializedInput")]
    [JsonProperty(PropertyName = "materializedInput")]
    public Dictionary<string, JsonElement> MaterializedInput { get; init; } = new();
}
