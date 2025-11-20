namespace Backend.Plugins.Communication.Protocols.Invocation;

public enum InvocationSignal : byte
{
    /// <summary>
    /// Initiator pushes the current global state snapshot prior to executing a job.
    /// </summary>
    GlobalState,

    /// <summary>
    /// Responder confirms it has received and applied the global state snapshot.
    /// </summary>
    GlobalStateAcknowledged,

    /// <summary>
    /// Initiator streams the invocation input payload.
    /// </summary>
    InvocationInput,

    /// <summary>
    /// Responder confirms the invocation payload is ready for execution.
    /// </summary>
    InvocationInputAcknowledged
}
