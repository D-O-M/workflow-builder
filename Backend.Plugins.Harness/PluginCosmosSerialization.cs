using System.Text.Encodings.Web;
using System.Text.Json;

namespace Backend.Plugins.Harness;

internal static class PluginCosmosSerialization
{
    public static JsonSerializerOptions CreateJsonOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
}
