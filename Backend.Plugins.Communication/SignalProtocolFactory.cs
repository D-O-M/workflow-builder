using System;
using Backend.Plugins.Communication.RingBuffers;

namespace Backend.Plugins.Communication;

/// <summary>
/// High-level factory that hides the details of the memory-mapped
/// implementation from consumers.
/// </summary>
public static class SignalProtocolFactory
{
    private const int DefaultCapacity = 64 * 1024; // 64 KB per direction

    /// <summary>
    /// Creates the metadata required for both parties to connect to the shared
    /// memory transports.
    /// </summary>
    /// <param name="connectionName">Friendly name used to derive unique map identifiers.</param>
    /// <param name="initiatorProcessId">Process id of the component that is emitting the handshake.</param>
    /// <param name="initiatorToResponderCapacity">Optional override for the initiator-to-responder buffer.</param>
    /// <param name="responderToInitiatorCapacity">Optional override for the responder-to-initiator buffer.</param>
    public static SignalHandshake CreateHandshake(
        string connectionName,
        int initiatorProcessId,
        int? initiatorToResponderCapacity = null,
        int? responderToInitiatorCapacity = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        var sessionId = Guid.NewGuid();
        var safeName = SanitizeName(connectionName);

        var initiatorToResponderName = $"sig-{safeName}-{sessionId:N}-i2r";
        var responderToInitiatorName = $"sig-{safeName}-{sessionId:N}-r2i";

        var i2rCapacity = ValidateCapacity(initiatorToResponderCapacity);
        var r2iCapacity = ValidateCapacity(responderToInitiatorCapacity);

        return new SignalHandshake(
            SignalHandshake.CurrentProtocolVersion,
            sessionId,
            initiatorToResponderName,
            responderToInitiatorName,
            i2rCapacity,
            r2iCapacity,
            initiatorToResponderName + "-mtx",
            responderToInitiatorName + "-mtx",
            initiatorProcessId,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates a channel bound to the memory-mapped files described by the
    /// provided handshake from the perspective of the specified role.
    /// </summary>
    public static ISignalChannel CreateChannel(SignalHandshake handshake, SignalEndpointRole role)
    {
        ArgumentNullException.ThrowIfNull(handshake);

        var outbound = role == SignalEndpointRole.Initiator
            ? new MemoryMappedRingBuffer(
                handshake.InitiatorToResponderMapName,
                handshake.InitiatorToResponderMutexName,
                handshake.InitiatorToResponderCapacity)
            : new MemoryMappedRingBuffer(
                handshake.ResponderToInitiatorMapName,
                handshake.ResponderToInitiatorMutexName,
                handshake.ResponderToInitiatorCapacity);

        var inbound = role == SignalEndpointRole.Initiator
            ? new MemoryMappedRingBuffer(
                handshake.ResponderToInitiatorMapName,
                handshake.ResponderToInitiatorMutexName,
                handshake.ResponderToInitiatorCapacity)
            : new MemoryMappedRingBuffer(
                handshake.InitiatorToResponderMapName,
                handshake.InitiatorToResponderMutexName,
                handshake.InitiatorToResponderCapacity);

        return new MemoryMappedSignalChannel(outbound, inbound);
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
            return "signal";
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
