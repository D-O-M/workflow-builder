namespace Backend.Plugins.Communication.Protocols.Invocation;

public interface IInvocationInitiator
{
    /// <summary>
    /// Sends a bare invocation signal over the ring buffer to the acceptor.
    /// </summary>
    Task SendSignalViaRingBuffer(InvocationSignal signal);

    /// <summary>
    /// Waits for the acceptor to reply with a bare signal over the ring buffer.
    /// </summary>
    Task AwaitResponseFromResponderViaRingBuffer(InvocationSignal signal);

    /// <summary>
    /// Sends the full global state snapshot so the acceptor can hydrate its environment.
    /// </summary>
    Task SendGlobalState();

    /// <summary>
    /// Waits for the acceptor to confirm the global state has been ingested.
    /// </summary>
    Task AwaitGlobalStateAcknowledgement();

    /// <summary>
    /// Sends the invocation input payload after state synchronization.
    /// </summary>
    Task SendInvocationInput();

    /// <summary>
    /// Waits for the acceptor to confirm the invocation payload is ready for execution.
    /// </summary>
    Task AwaitInvocationInputAcknowledgement();
}
