using System.Text;
using System.Text.Json;

namespace Backend.Plugins.Communication;

/// <summary>
/// Serialises the handshake into a compact Base64 payload that can be shipped
/// over STDIO without leaking implementation details to consumers.
/// </summary>
public static class NanoServiceHandshakeSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize(NanoServiceHandshake handshake)
    {
        ArgumentNullException.ThrowIfNull(handshake);
        var json = JsonSerializer.Serialize(handshake, Options);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static NanoServiceHandshake Deserialize(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        var handshake = JsonSerializer.Deserialize<NanoServiceHandshake>(json, Options)
            ?? throw new InvalidOperationException("Unable to read nano service handshake payload.");

        if (handshake.ProtocolVersion != NanoServiceHandshake.CurrentProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Protocol mismatch. Engine expects v{NanoServiceHandshake.CurrentProtocolVersion} but received v{handshake.ProtocolVersion}.");
        }

        return handshake;
    }
}
