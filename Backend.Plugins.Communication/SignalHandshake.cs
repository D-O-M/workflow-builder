namespace Backend.Plugins.Communication;

/// <summary>
/// Metadata exchanged over STDIO before both parties switch to the
/// memory-mapped protocol.
/// </summary>
/// <param name="ProtocolVersion">Version identifier for compatibility checks.</param>
/// <param name="SessionId">Correlation id shared by both participants.</param>
/// <param name="InitiatorToResponderMapName">Ring buffer carrying messages from the party that originated the handshake.</param>
/// <param name="ResponderToInitiatorMapName">Ring buffer flowing in the opposite direction.</param>
/// <param name="InitiatorToResponderCapacity">Byte capacity of the initiator-to-responder buffer.</param>
/// <param name="ResponderToInitiatorCapacity">Byte capacity of the responder-to-initiator buffer.</param>
/// <param name="InitiatorToResponderMutexName">Cross-process mutex protecting the initiator-to-responder ring.</param>
/// <param name="ResponderToInitiatorMutexName">Cross-process mutex protecting the responder-to-initiator ring.</param>
/// <param name="InitiatorProcessId">Process id of the party emitting the handshake.</param>
/// <param name="CreatedUtc">Timestamp to aid diagnostics.</param>
public sealed record SignalHandshake(
    byte ProtocolVersion,
    Guid SessionId,
    string InitiatorToResponderMapName,
    string ResponderToInitiatorMapName,
    int InitiatorToResponderCapacity,
    int ResponderToInitiatorCapacity,
    string InitiatorToResponderMutexName,
    string ResponderToInitiatorMutexName,
    int InitiatorProcessId,
    DateTimeOffset CreatedUtc)
{
    public const byte CurrentProtocolVersion = 1;
}
