using System;
using System.Text.Json;

namespace Backend.Plugins.Communication;

/// <summary>
/// Identifies the high-level class of data carried by a <see cref="SignalAction"/>.
/// </summary>
public enum SignalActionClass
{
    InputChannel,
    OutputChannel
}

/// <summary>
/// Metadata envelope that accompanies opaque payloads exchanged through the
/// signal channel.
/// </summary>
/// <param name="Class">Logical class of data being transported.</param>
/// <param name="Identifier">Caller-defined channel identifier.</param>
/// <param name="Type">Optional semantic type or model identifier.</param>
/// <param name="ContentType">Optional MIME-type describing the binary payload.</param>
/// <param name="Data">Opaque data bytes written to the channel.</param>
public sealed record SignalAction(
    SignalActionClass Class,
    string Identifier,
    string? Type,
    string? ContentType,
    byte[] Data);

/// <summary>
/// Helper responsible for serialising <see cref="SignalAction"/> envelopes.
/// </summary>
public static class SignalActionSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static ReadOnlyMemory<byte> Serialize(SignalAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return JsonSerializer.SerializeToUtf8Bytes(action, Options);
    }

    public static SignalAction Deserialize(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<SignalAction>(payload, Options)
            ?? throw new InvalidOperationException("Unable to read signal action payload.");
    }
}
