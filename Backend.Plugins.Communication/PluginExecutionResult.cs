using System.Text.Encodings.Web;
using System.Text.Json;

namespace Backend.Plugins.Communication;

public sealed record PluginExecutionResult(
    Guid JobId,
    bool Succeeded,
    Dictionary<string, JsonElement> OutputData,
    Dictionary<string, JsonElement> GlobalState,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? ErrorMessage);

public static class PluginExecutionResultSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize(PluginExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result, Options);
    }

    public static PluginExecutionResult Deserialize(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<PluginExecutionResult>(payload, Options)
            ?? throw new InvalidOperationException("Unable to read plugin execution result payload.");
    }
}
