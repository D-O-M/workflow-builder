using Backend.Plugins.Communication.Protocols;

namespace Backend.Plugins.Communication.Protocols.Invocation;

/// <summary>
/// Coordinates the data exchange for invocation requests. The initiator first
/// synchronizes global state and only proceeds with the invocation payload
/// once the acceptor acknowledges readiness.
/// </summary>
internal static class InvocationProtocol
{
    public static ProtocolDefinition<InvocationSignal, IInvocationInitiator, IInvocationAcceptor>
        Definition { get; } = Build();

    private static ProtocolDefinition<InvocationSignal, IInvocationInitiator, IInvocationAcceptor> Build()
    {
        var protocol = new ProtocolDefinition<InvocationSignal, IInvocationInitiator, IInvocationAcceptor>();

        protocol
            .AddInitiatorStep(
                InvocationSignal.GlobalState,
                CommunicationMethod.RingBuffer,
                i => i.SendGlobalState(),
                a => a.ReceiveGlobalState())
            .AddAcceptorStep(
                InvocationSignal.GlobalStateAcknowledged,
                CommunicationMethod.RingBuffer,
                a => a.SendGlobalStateAcknowledgement(),
                i => i.AwaitGlobalStateAcknowledgement())
            .AddInitiatorStep(
                InvocationSignal.InvocationInput,
                CommunicationMethod.RingBuffer,
                i => i.SendInvocationInput(),
                a => a.ReceiveInvocationInput())
            .AddAcceptorStep(
                InvocationSignal.InvocationInputAcknowledged,
                CommunicationMethod.RingBuffer,
                a => a.SendInvocationInputAcknowledgement(),
                i => i.AwaitInvocationInputAcknowledgement());

        return protocol;
    }
}
