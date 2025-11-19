namespace Backend.Plugins.Communication.Protocols.Handshake;

public interface IInitiator
{
    Task AwaitResponseFromResponderViaStandardOut(HandshakeSignal signal);
    Task AwaitResponseFromResponderViaRingBuffer(HandshakeSignal signal);
}

public interface IHandshakeInitiator : IInitiator
{
    Task SendSignalViaStandardIn(HandshakeSignal signal);
    Task SendSignalViaRingBuffer(HandshakeSignal signal);
    Task SendConfig();
    Task SendMmfFileHandles();
    Task SendTestViaRingBuffer();
    Task SendTestReceiptIfSuccessful();
    Task ReceiveAndVerifyConfigChecksum();
    Task ReceiveAndVerifyMmfFileHandlesChecksum();
    Task ReceiveTestViaRingBuffer();
    Task ReceiveAndVerifyTestReceipt();
}
