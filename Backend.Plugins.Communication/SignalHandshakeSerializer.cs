using System.Text;
using System.Text.Json;

namespace Backend.Plugins.Communication;

/// <summary>
/// Serialises the handshake into a compact Base64 payload that can be shipped
/// over STDIO without leaking implementation details to consumers.
/// </summary>
public static class SignalHandshakeSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize(SignalHandshake handshake)
    {
        ArgumentNullException.ThrowIfNull(handshake);
        var json = JsonSerializer.Serialize(handshake, Options);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static SignalHandshake Deserialize(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        var handshake = JsonSerializer.Deserialize<SignalHandshake>(json, Options)
            ?? throw new InvalidOperationException("Unable to read signal handshake payload.");

        if (handshake.ProtocolVersion != SignalHandshake.CurrentProtocolVersion)
        {
            throw new InvalidOperationException(
                $"Protocol mismatch. Expected v{SignalHandshake.CurrentProtocolVersion} but received v{handshake.ProtocolVersion}.");
        }

        return handshake;
    }
}
