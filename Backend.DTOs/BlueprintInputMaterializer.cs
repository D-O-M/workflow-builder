using System.Text.Json;

namespace Backend.Models;

public static class BlueprintInputMaterializer
{
    public static void ApplyInputPresence(ISet<string> available, IReadOnlyDictionary<string, JsonElement> values)
    {
        foreach (var (name, value) in values)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                available.Remove(name);
            else
                available.Add(name);
        }
    }

    public static Dictionary<string, JsonElement> MaterializePartialInput(
        JsonElement sourcePayload,
        IReadOnlyCollection<ExecutableBlueprintPropertyMapping> mappings)
    {
        var materialized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (sourcePayload.ValueKind != JsonValueKind.Object)
        {
            return materialized;
        }

        var sourceProperties = sourcePayload.EnumerateObject().ToList();
        var exactSourceProperties = sourceProperties
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

        foreach (var mapping in mappings)
        {
            if (exactSourceProperties.TryGetValue(mapping.SourceProperty, out var value) ||
                TryGetCompatibleSourceProperty(sourceProperties, mapping.SourceProperty, out value))
            {
                materialized[mapping.TargetProperty] = value.Clone();
            }
        }

        return materialized;
    }

    public static Dictionary<string, JsonElement> MergeInputs(
        IEnumerable<IReadOnlyDictionary<string, JsonElement>> partialInputs,
        IReadOnlyDictionary<string, JsonElement>? designTimeOverrides = null,
        IReadOnlyDictionary<string, JsonElement>? runtimeOverrides = null)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var partialInput in partialInputs)
        {
            foreach (var (key, value) in partialInput)
            {
                merged[key] = value.Clone();
            }
        }

        ApplyOverrides(merged, designTimeOverrides);
        ApplyOverrides(merged, runtimeOverrides);

        return merged;
    }

    public static Dictionary<string, JsonElement> BuildChannelPayload(
        string inputChannelName,
        IReadOnlyDictionary<string, JsonElement> materializedInput) =>
        new(StringComparer.Ordinal)
        {
            [inputChannelName] = JsonSerializer.SerializeToElement(materializedInput, JsonSerializerOptions.Web)
        };

    private static void ApplyOverrides(
        IDictionary<string, JsonElement> target,
        IReadOnlyDictionary<string, JsonElement>? overrides)
    {
        if (overrides is null)
        {
            return;
        }

        foreach (var (key, value) in overrides)
        {
            target[key] = value.Clone();
        }
    }

    private static bool TryGetCompatibleSourceProperty(
        IReadOnlyCollection<JsonProperty> sourceProperties,
        string sourceProperty,
        out JsonElement value)
    {
        foreach (var property in sourceProperties)
        {
            if (string.Equals(property.Name, sourceProperty, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
