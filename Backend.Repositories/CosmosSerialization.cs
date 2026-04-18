using System.Text.Encodings.Web;
using System.Text.Json;

namespace Backend.Repositories;

public static class CosmosSerialization
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
