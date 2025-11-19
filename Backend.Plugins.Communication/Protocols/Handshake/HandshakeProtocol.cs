namespace Backend.Plugins.Communication.Protocols.Handshake;

/// <summary>
/// Static protocol definition: pure data, no concrete initiator/acceptor instances.
/// This is the human-readable script for the handshake.
/// </summary>
internal static class HandshakeProtocol
{
    public static ProtocolDefinition<HandshakeSignal, IHandshakeInitiator, IHandshakeAcceptor>
        Definition { get; } = Build();

    private static ProtocolDefinition<HandshakeSignal, IHandshakeInitiator, IHandshakeAcceptor> Build()
    {
        var protocol = new ProtocolDefinition<HandshakeSignal, IHandshakeInitiator, IHandshakeAcceptor>();

        protocol
            .AddInitiatorStep(
                HandshakeSignal.Hello,
                CommunicationMethod.StandardIO)
            .AddAcceptorStep(
                HandshakeSignal.Acknowledge,
                CommunicationMethod.StandardIO)
            .AddInitiatorStep(
                HandshakeSignal.Setup,
                CommunicationMethod.StandardIO)
            .AddAcceptorStep(
                HandshakeSignal.Ready,
                CommunicationMethod.StandardIO)
            .AddInitiatorStep(
                HandshakeSignal.Data,
                CommunicationMethod.StandardIO,
                i => i.SendConfig(),
                a => a.ReceiveConfig())
            .AddAcceptorStep(
                HandshakeSignal.AcknowledgeWithReceipt,
                CommunicationMethod.StandardIO,
                a => a.SendChecksumForConfig(),
                i => i.ReceiveAndVerifyConfigChecksum())
            .AddInitiatorStep(
                HandshakeSignal.UpgradeCommsMethod,
                CommunicationMethod.StandardIO)
            .AddAcceptorStep(
                HandshakeSignal.Ready,
                CommunicationMethod.StandardIO)
            .AddInitiatorStep(
                HandshakeSignal.Data,
                CommunicationMethod.StandardIO,
                i => i.SendMmfFileHandles(),
                a => a.ReceiveMmfFileHandles())
            .AddAcceptorStep(
                HandshakeSignal.AcknowledgeWithReceipt,
                CommunicationMethod.StandardIO,
                a => a.SendMmfFileHandlesChecksum(),
                i => i.ReceiveAndVerifyMmfFileHandlesChecksum())
            .AddInitiatorStep(
                HandshakeSignal.ReadyForTest,
                CommunicationMethod.StandardIO)
            .AddAcceptorStep(
                HandshakeSignal.Test,
                CommunicationMethod.RingBuffer,
                a => a.SendTestViaRingBuffer(),
                i => i.ReceiveTestViaRingBuffer())
            .AddInitiatorStep(
                HandshakeSignal.AcknowledgeWithReceipt,
                CommunicationMethod.RingBuffer,
                i => i.SendTestReceiptIfSuccessful(),
                a => a.ReceiveAndVerifyTestReceipt())
            .AddAcceptorStep(
                HandshakeSignal.ReadyForTest,
                CommunicationMethod.RingBuffer)
            .AddInitiatorStep(
                HandshakeSignal.Test,
                CommunicationMethod.RingBuffer,
                i => i.SendTestViaRingBuffer(),
                a => a.ReceiveTestViaRingBuffer())
            .AddAcceptorStep(
                HandshakeSignal.AcknowledgeWithReceipt,
                CommunicationMethod.RingBuffer,
                a => a.SendTestReceiptIfSuccessful(),
                i => i.ReceiveAndVerifyTestReceipt())
            .AddInitiatorStep(
                HandshakeSignal.Ready,
                CommunicationMethod.RingBuffer)
            .AddAcceptorStep(
                HandshakeSignal.Ready,
                CommunicationMethod.RingBuffer);

        return protocol;
    }
}
