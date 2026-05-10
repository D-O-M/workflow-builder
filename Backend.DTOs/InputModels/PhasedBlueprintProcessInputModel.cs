using System.Text.Json;

namespace Backend.Models.InputModels;

public sealed class PhasedBlueprintProcessInputModel
{
    public Dictionary<string, JsonElement> GlobalState { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonElement> InitialTaskInput { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<Guid, Dictionary<string, JsonElement>> NodeOverrides { get; init; } = new();
}

public sealed class PhasedBlueprintTriggerInputModel
{
    public int? PhasedBlueprintVersionId { get; init; }

    public Dictionary<string, JsonElement> GlobalState { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonElement> TriggerInput { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<Guid, Dictionary<string, JsonElement>> NodeOverrides { get; init; } = new();
}

