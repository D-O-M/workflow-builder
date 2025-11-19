namespace Backend.Plugins.Communication.Protocols;

public struct SignalEnvelope<TSignal> where TSignal : struct, Enum
{
    public TSignal Signal { get; private init; }

    public SignalEnvelope(TSignal signal)
    {
        Signal = signal;
    }

    /// <summary>
    /// Serialize a signal-only envelope as:
    /// [ Signal (1 byte) ]
    /// </summary>
    public byte[] ToBytes()
    {
        byte signalByte = Convert.ToByte(Signal);
        return [signalByte];
    }
}

public struct SignalWithPayloadEnvelope<TSignal>
    where TSignal : struct, Enum
{
    public TSignal Signal { get; private init; }
    public byte[] Payload { get; private init; }

    public SignalWithPayloadEnvelope(TSignal signal, byte[] payload)
    {
        Signal = signal;
        Payload = payload;
    }

    /// <summary>
    /// Serialize a signal+payload envelope as:
    /// [ Signal (1 byte) ]
    /// [ PayloadLength (4 bytes, little-endian int) ]
    /// [ Payload (N bytes) ]
    /// </summary>
    public byte[] ToBytes()
    {
        byte signalByte = Convert.ToByte(Signal);

        byte[] payload = Payload ?? [];
        int length = payload.Length;

        // 1 (signal) + 4 (length) + payload
        byte[] buffer = new byte[1 + 4 + length];

        // signal
        buffer[0] = signalByte;

        // payload length (little-endian)
        BitConverter.GetBytes(length).CopyTo(buffer, 1);

        // payload
        if (length > 0)
        {
            Buffer.BlockCopy(payload, 0, buffer, 5, length);
        }

        return buffer;
    }
}

public static class SignalEnvelope
{
    /// <summary>
    /// Deserialize from:
    /// [ Signal (1 byte) ]
    /// </summary>
    public static SignalEnvelope<TSignal> FromBytes<TSignal>(byte[] bytes)
        where TSignal : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < 1)
            throw new ArgumentException("Buffer too short to contain a signal.", nameof(bytes));

        byte signalByte = bytes[0];

        var signal = (TSignal)Enum.ToObject(typeof(TSignal), signalByte);

        return new SignalEnvelope<TSignal>(signal);
    }
}

public static class SignalWithPayloadEnvelope
{
    /// <summary>
    /// Deserialize from:
    /// [ Signal (1 byte) ]
    /// [ PayloadLength (4 bytes, little-endian int) ]
    /// [ Payload (N bytes) ]
    /// </summary>
    public static SignalWithPayloadEnvelope<TSignal> FromBytes<TSignal>(byte[] bytes)
        where TSignal : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < 5)
            throw new ArgumentException(
                "Buffer too short to contain a signal and payload length.",
                nameof(bytes));

        // 1. Signal
        byte signalByte = bytes[0];
        var signal = (TSignal)Enum.ToObject(typeof(TSignal), signalByte);

        // 2. Payload length
        int length = BitConverter.ToInt32(bytes, 1);
        if (length < 0)
        {
            throw new ArgumentException("Negative payload length in buffer.", nameof(bytes));
        }

        if (bytes.Length < 5 + length)
        {
            throw new ArgumentException(
                "Buffer too short for declared payload length.",
                nameof(bytes));
        }

        // 3. Payload
        byte[] payload = new byte[length];
        if (length > 0)
        {
            Buffer.BlockCopy(bytes, 5, payload, 0, length);
        }

        return new SignalWithPayloadEnvelope<TSignal>(signal, payload);
    }
}
