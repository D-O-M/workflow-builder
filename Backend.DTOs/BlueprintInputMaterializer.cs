using System.Text.Json;

namespace Backend.Models;

public static class BlueprintInputMaterializer
{
    public static Dictionary<string, JsonElement> MaterializePartialInput(
        JsonElement sourcePayload,
        IReadOnlyCollection<ExecutableBlueprintPropertyMapping> mappings)
    {
        var materialized = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (sourcePayload.ValueKind != JsonValueKind.Object)
        {
            return materialized;
        }

        var sourceProperties = sourcePayload.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

        foreach (var mapping in mappings)
        {
            if (sourceProperties.TryGetValue(mapping.SourceProperty, out var value))
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
}
