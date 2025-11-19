namespace Backend.Plugins.Communication.Protocols.HandshakeProtocol;

internal interface IAcceptor
{
    Task AwaitMessageFromInitiatorViaStandardIn();
    Task AwaitMessageFromInitiatorViaRingBuffer();

    // Symmetric "bare signal" send operations
    Task SendSignalViaStandardOut(HandshakeSignal signal);
    Task SendSignalViaRingBuffer(HandshakeSignal signal);
}

internal interface IHandshakeAcceptor : IAcceptor
{
    Task SendChecksumForConfig();
    Task SendMmfFileHandlesReceiptAcknowledgement();
    Task SendTestViaRingBuffer();
    Task SendTestReceiptIfSuccessful();
}
