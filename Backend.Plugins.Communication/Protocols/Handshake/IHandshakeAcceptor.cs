namespace Backend.Plugins.Communication.Protocols.Handshake;

public interface IAcceptor
{
    Task AwaitMessageFromInitiatorViaStandardIn(HandshakeSignal signal);
    Task AwaitMessageFromInitiatorViaRingBuffer(HandshakeSignal signal);

    // Symmetric "bare signal" send operations
    Task SendSignalViaStandardOut(HandshakeSignal signal);
    Task SendSignalViaRingBuffer(HandshakeSignal signal);
}

public interface IHandshakeAcceptor : IAcceptor
{
    Task ReceiveConfig();
    Task SendChecksumForConfig();
    Task ReceiveMmfFileHandles();
    Task SendMmfFileHandlesChecksum();
    Task SendTestViaRingBuffer();
    Task ReceiveTestViaRingBuffer();
    Task SendTestReceiptIfSuccessful();
    Task ReceiveAndVerifyTestReceipt();
}
