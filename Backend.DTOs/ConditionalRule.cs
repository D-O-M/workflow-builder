using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Models;

public sealed record ConditionalRule
{
    public string Kind { get; set; } = "";
    public bool Negated { get; set; }
    public List<ConditionalRule> Children { get; set; } = [];
    public string? Field { get; set; }
    public string? Operator { get; set; }
    public bool IgnoreCase { get; set; }
    public List<ConditionalOperand> Operands { get; set; } = [];
}

public sealed record ConditionalOperand
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(ConditionalLiteralJsonConverter))]
    public JsonElement? Literal { get; set; }
}

// Numeric tokens posted by API clients retain their type during validation. On the
// outbound wire they use the same lossless text representation as the rule drawer.
internal sealed class ConditionalLiteralJsonConverter : JsonConverter<JsonElement?>
{
    public override JsonElement? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement? literal, JsonSerializerOptions options)
    {
        if (literal is not { } value)
            writer.WriteNullValue();
        else if (value.ValueKind == JsonValueKind.Number)
            writer.WriteStringValue(value.GetRawText());
        else
            value.WriteTo(writer);
    }
}

public sealed record ConditionalField
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Required { get; set; }
    public List<string> EnumValues { get; set; } = [];
}

public sealed record ConditionalRuleError(string Path, string Message);
