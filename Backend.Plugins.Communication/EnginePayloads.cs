using System.Text.Json;

namespace Backend.Plugins.Communication;

/// <summary>
/// Payload exchanged when the engine pushes a model into an input channel.
/// </summary>
public sealed record EngineChannelModelPayload(
    string ChannelName,
    string ModelType,
    string ContentType,
    byte[] ModelData);

/// <summary>
/// Payload exchanged when the engine pushes the global state snapshot.
/// </summary>
public sealed record EngineGlobalStatePayload(
    string StateType,
    string ContentType,
    byte[] StateData);

/// <summary>
/// Helper responsible for serialising payloads that travel through the ring
/// buffers.
/// </summary>
public static class EnginePayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static ReadOnlyMemory<byte> SerializeChannelModel(in EngineChannelModelPayload payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(payload, Options);
    }

    public static ReadOnlyMemory<byte> SerializeGlobalState(in EngineGlobalStatePayload payload)
    {
        return JsonSerializer.SerializeToUtf8Bytes(payload, Options);
    }

    public static EngineChannelModelPayload DeserializeChannelModel(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<EngineChannelModelPayload>(payload, Options)
            ?? throw new InvalidOperationException("Unable to read channel payload from engine message.");
    }

    public static EngineGlobalStatePayload DeserializeGlobalState(ReadOnlySpan<byte> payload)
    {
        return JsonSerializer.Deserialize<EngineGlobalStatePayload>(payload, Options)
            ?? throw new InvalidOperationException("Unable to read global state payload from engine message.");
    }
}
