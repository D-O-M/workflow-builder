namespace Backend.Plugins.Communication.Protocols.Invocation;

public interface IInvocationAcceptor
{
    /// <summary>
    /// Receives the global state snapshot from the initiator.
    /// </summary>
    Task ReceiveGlobalState();

    /// <summary>
    /// Confirms the global state snapshot was successfully applied.
    /// </summary>
    Task SendGlobalStateAcknowledgement();

    /// <summary>
    /// Receives the invocation input payload.
    /// </summary>
    Task ReceiveInvocationInput();

    /// <summary>
    /// Signals that the invocation payload is ready to be executed.
    /// </summary>
    Task SendInvocationInputAcknowledgement();
}
