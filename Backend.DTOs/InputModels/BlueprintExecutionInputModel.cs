using System.Text.Json;

namespace Backend.Models.InputModels;

public sealed class BlueprintExecutionInputModel
{
    public Dictionary<string, JsonElement> GlobalState { get; init; } = new();
    public Dictionary<string, JsonElement> InitialTaskInput { get; init; } = new();
}
