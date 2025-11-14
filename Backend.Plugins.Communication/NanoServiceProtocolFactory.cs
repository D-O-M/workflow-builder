using Backend.Plugins.Communication.RingBuffers;

namespace Backend.Plugins.Communication;

/// <summary>
/// High-level factory that hides the details of the memory-mapped
/// implementation from consumers.
/// </summary>
public static class NanoServiceProtocolFactory
{
    private const int DefaultCapacity = 64 * 1024; // 64 KB per direction

    /// <summary>
    /// Creates the metadata required for both parties to connect to the shared
    /// memory transports.
    /// </summary>
    /// <param name="nanoServiceName">Friendly name used to derive unique map identifiers.</param>
    /// <param name="engineProcessId">Owning engine process id for diagnostics.</param>
    /// <param name="engineToNanoCapacity">Optional override for the engine-to-nano buffer. Increase this value when the engine needs to push large state snapshots.</param>
    /// <param name="nanoToEngineCapacity">Optional override for the nano-to-engine buffer.</param>
    public static NanoServiceHandshake CreateHandshake(string nanoServiceName, int engineProcessId, int? engineToNanoCapacity = null, int? nanoToEngineCapacity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nanoServiceName);
        var sessionId = Guid.NewGuid();
        var safeName = SanitizeName(nanoServiceName);

        var engineToNanoName = $"ns-{safeName}-{sessionId:N}-e2n";
        var nanoToEngineName = $"ns-{safeName}-{sessionId:N}-n2e";

        var e2nCapacity = ValidateCapacity(engineToNanoCapacity);
        var n2eCapacity = ValidateCapacity(nanoToEngineCapacity);

        return new NanoServiceHandshake(
            NanoServiceHandshake.CurrentProtocolVersion,
            sessionId,
            engineToNanoName,
            nanoToEngineName,
            e2nCapacity,
            n2eCapacity,
            engineToNanoName + "-mtx",
            nanoToEngineName + "-mtx",
            engineProcessId,
            DateTimeOffset.UtcNow);
    }

    public static INanoServiceChannel CreateEngineChannel(NanoServiceHandshake handshake)
    {
        ArgumentNullException.ThrowIfNull(handshake);

        var outbound = new MemoryMappedRingBuffer(
            handshake.EngineToNanoMapName,
            handshake.EngineToNanoMutexName,
            handshake.EngineToNanoCapacity);

        var inbound = new MemoryMappedRingBuffer(
            handshake.NanoToEngineMapName,
            handshake.NanoToEngineMutexName,
            handshake.NanoToEngineCapacity);

        return new MemoryMappedNanoServiceChannel(outbound, inbound);
    }

    public static INanoServiceChannel CreateNanoServiceChannel(NanoServiceHandshake handshake)
    {
        ArgumentNullException.ThrowIfNull(handshake);

        var outbound = new MemoryMappedRingBuffer(
            handshake.NanoToEngineMapName,
            handshake.NanoToEngineMutexName,
            handshake.NanoToEngineCapacity);

        var inbound = new MemoryMappedRingBuffer(
            handshake.EngineToNanoMapName,
            handshake.EngineToNanoMutexName,
            handshake.EngineToNanoCapacity);

        return new MemoryMappedNanoServiceChannel(outbound, inbound);
    }

    private static string SanitizeName(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var idx = 0;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[idx++] = char.ToLowerInvariant(ch);
            }
            else if (idx == 0 || buffer[idx - 1] != '-')
            {
                buffer[idx++] = '-';
            }
        }

        if (idx == 0)
        {
            return "nano";
        }

        return new string(buffer[..idx]);
    }

    private static int ValidateCapacity(int? capacity)
    {
        if (capacity is null)
        {
            return DefaultCapacity;
        }

        if (capacity < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 256 bytes.");
        }

        return capacity.Value;
    }
}
