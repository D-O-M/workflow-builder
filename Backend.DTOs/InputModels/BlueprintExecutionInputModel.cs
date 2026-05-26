using System.Text.Json;

namespace Backend.Models.InputModels;

public sealed class BlueprintProcessInputModel
{
    public Dictionary<string, JsonElement> InitialTaskInput { get; init; } = new();
    public Dictionary<Guid, Dictionary<string, JsonElement>> NodeOverrides { get; init; } = new();
}
