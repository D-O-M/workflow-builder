namespace Backend.Plugins.Communication.Protocols.Handshake;

public interface IInitiator
{
    Task AwaitResponseFromResponderViaStandardOut();
    Task AwaitResponseFromResponderViaRingBuffer();
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
