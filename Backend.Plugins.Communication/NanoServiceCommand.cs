namespace Backend.Plugins.Communication;

/// <summary>
/// A byte-sized command identifier that prefixes every protocol message.
/// The values are intentionally compact to keep bandwidth usage low while
/// still allowing the engine and nano services to exchange rich semantics.
/// </summary>
public enum NanoServiceCommand : byte
{
    None = 0,

    /// <summary>
    /// A short keep-alive signal exchanged periodically to keep the ring
    /// buffers hot and prove liveness of both parties.
    /// </summary>
    Heartbeat = 1,

    /// <summary>
    /// Used to acknowledge receipt of a message that requires positive
    /// confirmation (state load, model updates, etc.).
    /// </summary>
    Acknowledge = 2,

    /// <summary>
    /// A command forwarded by the engine. The nano service does not need to
    /// know (or care) which upstream transport produced it.
    /// </summary>
    EngineDispatch = 3,

    /// <summary>
    /// Pushes a serialized state snapshot from the engine into the nano
    /// service. The payload is read directly from the engine-to-nano memory
    /// mapped file, keeping the nano service agnostic of how the engine
    /// sourced the snapshot.
    /// </summary>
    StatePush = 4,

    /// <summary>
    /// Pushes a model, document, or artifact into the nano service. Models
    /// are always delivered proactively by the engine; the nano service never
    /// services read requests.
    /// </summary>
    ModelWrite = 5,

    /// <summary>
    /// Allows a nano service to proactively publish a model, document, or
    /// artifact back to the engine without a preceding request. This keeps the
    /// protocol fully push-based in both directions.
    /// </summary>
    ModelPublish = 6,

    /// <summary>
    /// Signals that the nano service encountered a fault during command
    /// execution. The payload contains structured diagnostic data.
    /// </summary>
    Fault = 240,

    /// <summary>
    /// Asks the nano service to gracefully shut down.
    /// </summary>
    Shutdown = 241
}
