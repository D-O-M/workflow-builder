namespace Backend.Plugins.Communication;

/// <summary>
/// Byte-sized verb that prefixes every protocol message flowing through the
/// duplex channels. Commands are intentionally compact to minimize bandwidth
/// while remaining expressive enough for both endpoints.
/// </summary>
public enum SignalCommand : byte
{
    None = 0,

    /// <summary>
    /// Short keep-alive exchanged by both parties to prove liveness.
    /// </summary>
    Heartbeat = 1,

    /// <summary>
    /// Positive confirmation that a message was processed successfully.
    /// </summary>
    Acknowledge = 2,

    /// <summary>
    /// Optional command forwarding primitive. The semantics are owned by the
    /// caller and can represent any higher-level signal.
    /// </summary>
    Dispatch = 3,

    /// <summary>
    /// Pushes data bytes accompanied by a <see cref="SignalAction"/> metadata
    /// envelope. Both endpoints use the same shape for input, output, and
    /// global-state transfers.
    /// </summary>
    Action = 4,

    /// <summary>
    /// Represents an invocation with a new set of data to execute on.
    /// </summary>
    Invocation = 5,

    /// <summary>
    /// Structured fault information published by either party.
    /// </summary>
    Fault = 240,

    /// <summary>
    /// Politely asks the counterpart to shut down.
    /// </summary>
    Shutdown = 241
}
